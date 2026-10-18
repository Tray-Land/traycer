using System.Net.Sockets;
using System.Net.WebSockets;
using TrayCer.Core.Drivers;
using TrayCer.Core.Models;
using TrayCer.Core.Net;

namespace TrayCer.Core.Monitoring;

/// <summary>A reading (or failure) and the event it produced, if any.</summary>
public sealed record PrinterUpdate(PrinterStatus Status, PrintEvent? Event);

/// <summary>
/// Watches one printer on a background loop: one request at a time, quick polls while printing or
/// while the flyout is open, slow ones while idle, and backoff while unreachable. Raises
/// <see cref="Updated"/> on a thread-pool thread after every attempt.
/// </summary>
public sealed class PrinterMonitor : IAsyncDisposable
{
    private static readonly TimeSpan PollTimeout = TimeSpan.FromSeconds(20);

    private readonly IPrinterDriver _driver;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly CancellationTokenSource _stop = new();
    private readonly Lock _gate = new();
    private PrinterStatus _status;
    private PrinterSnapshot? _lastGood;
    private MonitorOptions _options;
    private Task? _loop;
    private int _failures;
    private volatile bool _foreground;

    public PrinterMonitor(PrinterConfig config, IPrinterDriver driver, MonitorOptions options, TimeProvider? time = null)
    {
        _driver = driver;
        _options = options;
        _time = time ?? TimeProvider.System;
        _status = new PrinterStatus { Config = config };
    }

    public event EventHandler<PrinterUpdate>? Updated;

    public PrinterStatus Status
    {
        get
        {
            lock (_gate)
            {
                return _status;
            }
        }
    }

    public IPrinterDriver Driver => _driver;

    public MonitorOptions Options
    {
        get => _options;
        set => _options = value;
    }

    /// <summary>True while the flyout is open: poll at the active interval and keep connections up.</summary>
    public bool Foreground
    {
        get => _foreground;
        set
        {
            bool wasForeground = _foreground;
            _foreground = value;
            if (value && !wasForeground)
            {
                RefreshNow();
            }
        }
    }

    public void Start() => _loop ??= Task.Run(RunAsync);

    /// <summary>Cuts the current wait short; no-op if a poll is already due.</summary>
    public void RefreshNow()
    {
        lock (_gate)
        {
            if (!_stop.IsCancellationRequested && _wake.CurrentCount == 0)
            {
                _wake.Release();
            }
        }
    }

    /// <summary>The network changed: forget the backoff and look again now.</summary>
    public void Retry()
    {
        Interlocked.Exchange(ref _failures, 0);
        RefreshNow();
    }

    /// <summary>The user has seen this printer's news; drop the tray flag.</summary>
    public PrinterStatus Acknowledge()
    {
        lock (_gate)
        {
            _status = _status with { Attention = AttentionLevel.None };
            return _status;
        }
    }

    public async ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            _stop.Cancel();
        }

        if (_loop is not null)
        {
            try
            {
                await _loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Stopping.
            }
        }

        await _driver.DisposeAsync().ConfigureAwait(false);
        _stop.Dispose();
        _wake.Dispose();
    }

    private async Task RunAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            await PollOnceAsync().ConfigureAwait(false);
            if (_stop.IsCancellationRequested)
            {
                break;
            }

            MonitorOptions options = _options;
            TimeSpan delay = PollPolicy.NextDelay(_lastGood, _failures, _foreground, _driver.IsPersistent, options);
            if (PollPolicy.ShouldSuspend(_lastGood, _foreground, _driver.IsPersistent))
            {
                await _driver.SuspendAsync().ConfigureAwait(false);
            }

            lock (_gate)
            {
                _status = _status with { NextCheck = _time.GetUtcNow() + delay };
            }

            try
            {
                await _wake.WaitAsync(delay, _stop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    internal async Task PollOnceAsync()
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        timeout.CancelAfter(PollTimeout);
        PrinterUpdate update;
        try
        {
            PrinterSnapshot snapshot = await _driver.GetSnapshotAsync(timeout.Token).ConfigureAwait(false);
            update = Succeeded(snapshot);
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            update = Failed(ex);
            try
            {
                await _driver.SuspendAsync().ConfigureAwait(false);
            }
            catch
            {
                // A broken connection may fail to close; it's being dropped either way.
            }
        }

        Updated?.Invoke(this, update);
    }

    private PrinterUpdate Succeeded(PrinterSnapshot snapshot)
    {
        DateTimeOffset now = _time.GetUtcNow();
        Interlocked.Exchange(ref _failures, 0);
        PrintEventKind? kind = TransitionPolicy.Detect(_lastGood, snapshot);
        PrintEvent? printEvent = kind is { } k
            ? new PrintEvent(k, snapshot.JobName ?? _lastGood?.JobName, snapshot.Message, now)
            : null;
        _lastGood = snapshot;

        lock (_gate)
        {
            _status = _status with
            {
                Snapshot = snapshot,
                Connection = ConnectionState.Connected,
                Problem = null,
                LastUpdated = now,
                LastEvent = printEvent ?? _status.LastEvent,
                Attention = TransitionPolicy.AttentionAfter(kind, _status.Attention),
            };
            return new PrinterUpdate(_status, printEvent);
        }
    }

    private PrinterUpdate Failed(Exception ex)
    {
        DateTimeOffset now = _time.GetUtcNow();
        Interlocked.Increment(ref _failures);
        lock (_gate)
        {
            bool recent = _status.LastUpdated is { } last && now - last < _options.ReconnectGrace;
            ConnectionState connection = ex switch
            {
                PrinterAuthException => ConnectionState.AuthFailed,
                PrinterProtocolException => ConnectionState.Incompatible,
                _ when _status.Connection == ConnectionState.Connecting => ConnectionState.Unreachable,
                _ => recent ? ConnectionState.Reconnecting : ConnectionState.Unreachable,
            };

            _status = _status with { Connection = connection, Problem = Describe(ex, everConnected: _status.LastUpdated is not null) };
            return new PrinterUpdate(_status, null);
        }
    }

    /// <summary>A failure in words the user can act on.</summary>
    /// <param name="everConnected">True when this printer has answered before, so a refused connection
    /// means it's off or restarting rather than set up wrong.</param>
    public static string Describe(Exception ex, bool everConnected = false)
    {
        Exception root = ex;
        while (root.InnerException is not null && root is HttpRequestException or WebSocketException or IOException)
        {
            root = root.InnerException;
        }

        return ex switch
        {
            PrinterAuthException or PrinterProtocolException => ex.Message,
            OperationCanceledException or TimeoutException => "The printer didn't answer in time.",
            HttpRequestException { StatusCode: not null } http => http.Message,
            _ => root switch
            {
                SocketException { SocketErrorCode: SocketError.ConnectionRefused } when everConnected => "The printer isn't accepting connections. It may be off or restarting.",
                SocketException { SocketErrorCode: SocketError.ConnectionRefused } => "The printer refused the connection. Check the printer type and port.",
                SocketException { SocketErrorCode: SocketError.HostNotFound or SocketError.NoData } => "Can't find a printer at that address.",
                SocketException { SocketErrorCode: SocketError.TimedOut or SocketError.HostUnreachable or SocketError.NetworkUnreachable } => "The printer isn't answering. It may be off.",
                System.Security.Authentication.AuthenticationException => "The secure connection to the printer failed.",
                _ => "Can't reach the printer. It may be off.",
            },
        };
    }
}
