using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TrayCer.Core.Monitoring;
using TrayCer.Core.Presentation;
using TrayCer.Services;
using TrayCer.Views;
using WinUIEx;

namespace TrayCer;

/// <summary>
/// Tray-only application: no window at launch, just a notification-area icon that shows print
/// progress and toggles the flyout. Windows exist only while they're in use; the idle app is an
/// icon, the printer monitors, and little else.
/// </summary>
public partial class App : Application
{
    public const string DisplayName = "Traycer";

    private const string MutexName = "Local\\TrayCer_SingleInstance";
    private const string ShowEventName = "Local\\TrayCer_ShowFlyout";

    // Startup leaves JIT and first-request state behind that the idle app never touches again.
    private static readonly TimeSpan StartupTrimDelay = TimeSpan.FromSeconds(30);

    private readonly TrayIconRenderer _trayRenderer = new();
    private Mutex? _singleInstanceMutex;
    private EventWaitHandle? _showEvent;
    private RegisteredWaitHandle? _showWait;
    private TrayIcon? _trayIcon;
    private TrayFlyoutWindow? _flyout;
    private DispatcherQueue? _dispatcher;
    private PrinterHub? _hub;
    private bool _isExiting;

    public App()
    {
        InitializeComponent();

        // A tray app lives on after its windows close; only the Exit menu ends it.
        DispatcherShutdownMode = DispatcherShutdownMode.OnExplicitShutdown;
        UnhandledException += (_, e) => LogCrash(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => LogCrash(e.ExceptionObject as Exception);
    }

    public static new App Current => (App)Application.Current;

    /// <summary>The printer monitors; created at launch and alive until exit.</summary>
    internal PrinterHub Hub => _hub ?? throw new InvalidOperationException("The app hasn't launched.");

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _singleInstanceMutex = new Mutex(true, MutexName, out bool createdNew);
        if (!createdNew)
        {
            // Another instance owns the tray icon: ask it to open its flyout, then quit.
            try
            {
                // Let the running instance take the foreground when it shows its flyout.
                Windows.Win32.PInvoke.AllowSetForegroundWindow(unchecked((uint)-1)); // ASFW_ANY
                using EventWaitHandle existing = EventWaitHandle.OpenExisting(ShowEventName);
                existing.Set();
            }
            catch
            {
                // The other instance may be shutting down; nothing to do.
            }

            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
            Exit();
            return;
        }

        _dispatcher = DispatcherQueue.GetForCurrentThread();
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        _showWait = ThreadPool.RegisterWaitForSingleObject(_showEvent,
            (_, _) => _dispatcher.TryEnqueue(ShowFlyout), null, Timeout.Infinite, executeOnlyOnce: false);

        NotificationService.Opened += (_, _) => _dispatcher.TryEnqueue(ShowFlyout);
        NotificationService.Register();

        InitializeTrayIcon();

        _hub = new PrinterHub(_dispatcher);
        _hub.Changed += (_, _) => UpdateTrayIcon();
        _hub.PrintEventRaised += Hub_PrintEventRaised;
        _hub.Start();
        UpdateTrayIcon();

        // First run: show where the app lives instead of launching into silence.
        if (!SettingsService.HasLaunchedBefore)
        {
            SettingsService.HasLaunchedBefore = true;
            ShowFlyout();
        }
        else
        {
            MemoryService.ReleaseIdle(StartupTrimDelay);
        }
    }

    public void ShowFlyout()
    {
        TrayFlyoutWindow flyout = EnsureFlyout();
        flyout.HidePopup();
        flyout.ReturnHomeIfStale();
        flyout.ShowPopup();
    }

    public void ShowSettings() => ShowPage(f => f.ShowSettings());

    private void ShowPage(Action<TrayFlyoutWindow> navigate)
    {
        TrayFlyoutWindow flyout = EnsureFlyout();
        navigate(flyout);
        if (!flyout.IsPopupVisible)
        {
            flyout.ShowPopup();
        }
    }

    /// <summary>The flyout is on screen: poll every printer closely, and its news has been seen.</summary>
    internal void OnFlyoutShown()
    {
        Hub.Foreground = true;
        Hub.AcknowledgeAll();
        NotificationService.ClearAll();
    }

    internal void OnFlyoutHidden() => Hub.Foreground = false;

    private void InitializeTrayIcon()
    {
        _trayIcon = new TrayIcon(1, TrayIconPath, DisplayName);
        _trayIcon.Selected += (_, _) => EnsureFlyout().Toggle();
        _trayIcon.ContextMenu += TrayIcon_ContextMenu;
        _trayIcon.IsVisible = true;
        WindowPlacementService.SetTrayIcon(_trayIcon);
        SystemThemeService.Changed += (_, _) => _dispatcher?.TryEnqueue(() =>
        {
            _trayRenderer.Invalidate();
            UpdateTrayIcon();
        });
    }

    // A white glyph disappears on a light taskbar, which follows the Windows theme, not the app theme.
    private static string TrayIconPath => Path.Combine(
        AppContext.BaseDirectory, "Assets", SystemThemeService.IsLight ? "AppIcon-dark.ico" : "AppIcon.ico");

    /// <summary>Redraws the tray icon and tooltip from every printer's latest status.</summary>
    private void UpdateTrayIcon()
    {
        if (_trayIcon is null || _hub is null)
        {
            return;
        }

        TrayIndicator indicator = TrayIndicator.Compute(_hub.Statuses, DisplayName);
        _trayRenderer.Show(_trayIcon, indicator, SystemThemeService.IsLight, TrayIconPath);
        _trayIcon.Tooltip = indicator.Tooltip;
    }

    private void Hub_PrintEventRaised(object? sender, (PrinterStatus Status, PrintEvent Event) e)
    {
        bool wanted = e.Event.Kind switch
        {
            PrintEventKind.Finished => SettingsService.NotifyWhenFinished,
            PrintEventKind.Failed or PrintEventKind.Error or PrintEventKind.Paused or PrintEventKind.NeedsAttention => SettingsService.NotifyAboutProblems,
            _ => false,
        };

        // No toast for news the user is already looking at.
        if (wanted && _flyout?.IsPopupVisible != true && StatusText.Notification(e.Status.Config.Name, e.Event) is { } toast)
        {
            NotificationService.Show(e.Status.Config.Id, toast.Title, toast.Body);
        }

        if (_flyout?.IsPopupVisible == true)
        {
            Hub.AcknowledgeAll();
        }
    }

    private void TrayIcon_ContextMenu(TrayIcon sender, TrayIconEventArgs args)
    {
        MenuFlyout menu = new();
        MenuFlyoutItem open = new() { Text = $"Open {DisplayName}", Icon = new FontIcon { Glyph = "" } };
        open.Click += (_, _) => ShowFlyout();
        menu.Items.Add(open);

        if (Hub.Statuses.Count > 0)
        {
            MenuFlyoutItem refresh = new() { Text = "Refresh printers", Icon = new FontIcon { Glyph = "" } };
            refresh.Click += (_, _) => Hub.RefreshAll();
            menu.Items.Add(refresh);
        }

        MenuFlyoutItem find = new() { Text = "Find printers", Icon = new FontIcon { Glyph = "" } };
        find.Click += (_, _) => ShowPage(f => f.ShowDiscover());
        MenuFlyoutItem add = new() { Text = "Add a printer", Icon = new FontIcon { Glyph = "" } };
        add.Click += (_, _) => ShowPage(f => f.ShowAddPrinter());
        MenuFlyoutItem settings = new() { Text = "Settings", Icon = new FontIcon { Glyph = "" } };
        settings.Click += (_, _) => ShowSettings();
        MenuFlyoutItem exit = new() { Text = "Exit", Icon = new FontIcon { Glyph = "" } };
        exit.Click += (_, _) => ExitApp();

        menu.Items.Add(find);
        menu.Items.Add(add);
        menu.Items.Add(settings);
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(exit);
        args.Flyout = menu;
    }

    private TrayFlyoutWindow EnsureFlyout()
    {
        if (_flyout is null)
        {
            // The flyout closes itself after staying hidden for a while; the next open builds a new one.
            _flyout = new TrayFlyoutWindow();
            _flyout.Closed += (sender, _) =>
            {
                _flyout = null;
                _hub?.ReleaseThumbnails();
                ReleaseIdleResourcesIfNoWindows(sender);
            };
        }

        return _flyout;
    }

    private void ReleaseIdleResourcesIfNoWindows(object closedWindow)
    {
        if (_flyout is null && !_isExiting)
        {
            MemoryService.ReleaseIdle(closedWindow: closedWindow);
        }
    }

    private async void ExitApp()
    {
        _isExiting = true;
        _flyout?.CloseWindow();

        if (_trayIcon is not null)
        {
            _trayIcon.IsVisible = false;
            _trayIcon.Dispose();
            _trayIcon = null;
        }

        _trayRenderer.Release();
        NotificationService.Unregister();
        _showWait?.Unregister(null);
        _showEvent?.Dispose();
        _singleInstanceMutex?.ReleaseMutex();
        _singleInstanceMutex?.Dispose();

        if (_hub is not null)
        {
            // Close MQTT and WebSocket sessions politely, but don't hang the exit on a dead printer.
            await Task.WhenAny(_hub.StopAsync(), Task.Delay(TimeSpan.FromSeconds(2)));
        }

        Exit();
    }

    private static void LogCrash(Exception? ex)
    {
        try
        {
            string path = Path.Combine(Path.GetTempPath(), "TrayCer-crash.log");
            File.AppendAllText(path, $"[{DateTime.Now:O}] {ex}{Environment.NewLine}");
        }
        catch
        {
            // Never throw from the crash logger.
        }
    }
}
