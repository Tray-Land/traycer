using Microsoft.UI.Dispatching;
using TrayCer.Core.Drivers;
using TrayCer.Core.Models;
using TrayCer.Core.Monitoring;

namespace TrayCer.Services;

/// <summary>
/// Runs one <see cref="PrinterMonitor"/> per saved printer for the app's whole life and keeps
/// their latest status on the UI thread.
/// </summary>
/// <remarks>
/// This is the documented exception to "no work while hidden": the tray icon itself shows print
/// progress, so printers are watched with no window open. It stays cheap: an idle printer gets one
/// request a minute (push printers a reconnect every three), a printing one every 15 seconds by
/// default, and an unreachable one backs off to every five minutes.
/// </remarks>
internal sealed class PrinterHub
{
    private const int MaxCachedThumbnails = 8;

    private readonly DispatcherQueue _dispatcher;
    private readonly Dictionary<string, PrinterMonitor> _monitors = [];
    private readonly Dictionary<string, PrinterStatus> _statuses = [];
    private readonly Dictionary<string, byte[]?> _thumbnails = [];

    // Printers with a finished print to mark complete; saved, so it outlives restarts and edits.
    private readonly HashSet<string> _needsMarkComplete = [];
    private bool _foreground;

    public PrinterHub(DispatcherQueue dispatcher)
    {
        _dispatcher = dispatcher;
    }

    /// <summary>Raised on the UI thread whenever a printer's status changes, or the set of printers does.</summary>
    public event EventHandler? Changed;

    /// <summary>Raised on the UI thread for print events worth telling the user about.</summary>
    public event EventHandler<(PrinterStatus Status, PrintEvent Event)>? PrintEventRaised;

    /// <summary>Every printer's status, in the order they were added.</summary>
    public IReadOnlyList<PrinterStatus> Statuses =>
        [.. PrinterStore.All.Select(c => _statuses.TryGetValue(c.Id, out PrinterStatus? s) ? s : new PrinterStatus { Config = c })];

    public PrinterStatus? Get(string id) => _statuses.GetValueOrDefault(id);

    public void Start()
    {
        _needsMarkComplete.UnionWith(SettingsService.NeedsMarkComplete);
        foreach (PrinterConfig config in PrinterStore.All)
        {
            StartMonitor(config);
        }

        PrinterStore.Changed += OnPrinterChanged;
        SettingsService.MonitoringChanged += OnMonitoringChanged;
        ConnectivityService.NetworkStatusChanged += OnNetworkStatusChanged;
    }

    /// <summary>True while the flyout is open: every printer polls at the active interval.</summary>
    public bool Foreground
    {
        get => _foreground;
        set
        {
            _foreground = value;
            foreach (PrinterMonitor monitor in _monitors.Values)
            {
                monitor.Foreground = value;
            }
        }
    }

    public void RefreshAll()
    {
        foreach (PrinterMonitor monitor in _monitors.Values)
        {
            monitor.Retry();
        }
    }

    /// <summary>The user opened the flyout and has seen every printer's news.</summary>
    public void AcknowledgeAll()
    {
        bool changed = false;
        foreach ((string id, PrinterMonitor monitor) in _monitors)
        {
            if (monitor.Status.Attention != AttentionLevel.None)
            {
                _statuses[id] = monitor.Acknowledge();
                changed = true;
            }
        }

        if (changed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>The user has dealt with a finished print: the tray goes back to normal for this printer.</summary>
    public void MarkComplete(string id)
    {
        if (_monitors.GetValueOrDefault(id) is { } monitor)
        {
            _statuses[id] = monitor.MarkComplete();
        }
        else if (_statuses.GetValueOrDefault(id) is { } status)
        {
            _statuses[id] = status with { NeedsMarkComplete = false };
        }

        RememberNeedsMarkComplete(id, false);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public Uri? WebInterface(string id) => _monitors.GetValueOrDefault(id)?.Driver.WebInterface;

    /// <summary>A job preview, cached while the flyout is around; null when the printer has none.</summary>
    public async Task<byte[]?> GetThumbnailAsync(string id, string url, CancellationToken cancellationToken)
    {
        if (_thumbnails.TryGetValue(url, out byte[]? cached))
        {
            return cached;
        }

        if (_monitors.GetValueOrDefault(id) is not { } monitor)
        {
            return null;
        }

        byte[]? bytes = null;
        try
        {
            bytes = await monitor.Driver.GetThumbnailAsync(url, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // No preview is fine; the card lays out without one.
        }

        if (_thumbnails.Count >= MaxCachedThumbnails)
        {
            _thumbnails.Clear();
        }

        _thumbnails[url] = bytes;
        return bytes;
    }

    /// <summary>The flyout closed for real: let its previews go.</summary>
    public void ReleaseThumbnails() => _thumbnails.Clear();

    public async Task StopAsync()
    {
        PrinterStore.Changed -= OnPrinterChanged;
        SettingsService.MonitoringChanged -= OnMonitoringChanged;
        ConnectivityService.NetworkStatusChanged -= OnNetworkStatusChanged;
        PrinterMonitor[] monitors = [.. _monitors.Values];
        _monitors.Clear();
        await Task.WhenAll(monitors.Select(m => m.DisposeAsync().AsTask()));
    }

    private static MonitorOptions CurrentOptions() => new() { ActiveInterval = SettingsService.ActiveInterval };

    private void StartMonitor(PrinterConfig config)
    {
        bool needsMarkComplete = _needsMarkComplete.Contains(config.Id);
        IPrinterDriver driver;
        try
        {
            driver = PrinterDriverFactory.Create(config, PrinterStore.SecretFor(config.Id));
        }
        catch (Exception ex) when (ex is FormatException or NotSupportedException)
        {
            _statuses[config.Id] = new PrinterStatus
            {
                Config = config,
                Connection = ConnectionState.Incompatible,
                Problem = "The printer's address isn't valid. Edit it.",
                NeedsMarkComplete = needsMarkComplete,
            };
            return;
        }

        PrinterMonitor monitor = new(config, driver, CurrentOptions(), needsMarkComplete: needsMarkComplete) { Foreground = _foreground };
        monitor.Updated += OnMonitorUpdated;
        _monitors[config.Id] = monitor;
        _statuses[config.Id] = monitor.Status;
        monitor.Start();
    }

    private void StopMonitor(string id)
    {
        _statuses.Remove(id);
        if (_monitors.Remove(id, out PrinterMonitor? monitor))
        {
            monitor.Updated -= OnMonitorUpdated;
            _ = DisposeQuietlyAsync(monitor);
        }
    }

    private static async Task DisposeQuietlyAsync(PrinterMonitor monitor)
    {
        try
        {
            await monitor.DisposeAsync();
        }
        catch
        {
            // A driver that fails to close is being thrown away anyway.
        }
    }

    // Raised on a thread-pool thread by each monitor.
    private void OnMonitorUpdated(object? sender, PrinterUpdate update) => _dispatcher.TryEnqueue(() =>
    {
        if (sender is not PrinterMonitor monitor || !_monitors.ContainsValue(monitor))
        {
            return; // Replaced or removed while the update was in flight.
        }

        // The monitor's latest, not the update's: the user may have acknowledged or marked the
        // print complete while this was queued, and the update would bring the flag back.
        PrinterStatus status = monitor.Status;
        _statuses[status.Config.Id] = status;
        RememberNeedsMarkComplete(status.Config.Id, status.NeedsMarkComplete);
        if (update.Event is { } printEvent)
        {
            PrintEventRaised?.Invoke(this, (update.Status, printEvent));
        }

        Changed?.Invoke(this, EventArgs.Empty);
    });

    private void OnPrinterChanged(object? sender, string id)
    {
        StopMonitor(id);
        if (PrinterStore.Find(id) is { } config)
        {
            StartMonitor(config); // An edit keeps a finished print waiting to be marked complete.
        }
        else
        {
            RememberNeedsMarkComplete(id, false);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void RememberNeedsMarkComplete(string id, bool needed)
    {
        if (needed ? _needsMarkComplete.Add(id) : _needsMarkComplete.Remove(id))
        {
            SettingsService.NeedsMarkComplete = [.. _needsMarkComplete];
        }
    }

    private void OnMonitoringChanged(object? sender, EventArgs e)
    {
        MonitorOptions options = CurrentOptions();
        foreach (PrinterMonitor monitor in _monitors.Values)
        {
            monitor.Options = options;
            monitor.RefreshNow();
        }
    }

    // Raised on a background thread; a network change usually means printers are reachable again.
    private void OnNetworkStatusChanged(object? sender, EventArgs e) => _dispatcher.TryEnqueue(RefreshAll);
}
