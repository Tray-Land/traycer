using System.Diagnostics;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using TrayCer.Controls;
using TrayCer.Services;
using Windows.Graphics;
using Windows.UI.ViewManagement;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;
using Windows.Win32.UI.WindowsAndMessaging;
using WinRT.Interop;
using WinUIEx;

namespace TrayCer.Views;

/// <summary>
/// Borderless, always-on-top popup shown against the tray icon, styled like the shell's own
/// flyouts. Slides and fades in, light-dismisses when focus leaves. Dismissing only hides it, so
/// reopening soon after is instant; after <see cref="CloseDelay"/> hidden it closes for real and
/// the next open builds a new window, so the idle app holds no XAML tree.
/// </summary>
public sealed partial class TrayFlyoutWindow : WindowEx
{
    private const int PopupWidth = 380;
    private const int PopupHeight = 560;

    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_LAYERED = 0x00080000;

    // The backdrop paints behind the XAML content and ignores UIElement.Opacity, so the fade uses
    // per-window layered alpha driven by a frame timer, alongside a small slide.
    private const int ShowSlideDistance = 24;
    private const int HideSlideDistance = 12;
    private const double ShowDurationMs = 250;
    private const double HideDurationMs = 180;

    /// <summary>
    /// A tray click right after a light-dismiss is the same click that dismissed the popup (focus
    /// left to the taskbar); treat it as "close" instead of re-showing.
    /// </summary>
    private static readonly TimeSpan RecentDismissWindow = TimeSpan.FromMilliseconds(300);

    private static readonly TimeSpan CloseDelay = TimeSpan.FromMinutes(1);

    /// <summary>A drilled-in page survives a quick dismiss; after this long hidden, a tray click opens home.</summary>
    private static readonly TimeSpan KeepPageFor = TimeSpan.FromSeconds(30);

    private readonly HWND _hwnd;
    private readonly FlyoutPage _page;
    private readonly Stack<FrameworkElement> _backStack = new();
    private readonly ShellBackdrop _backdrop = new();
    private readonly UISettings _uiSettings = new();
    private readonly DispatcherQueueTimer _hideTimer;
    private readonly DispatcherQueueTimer _closeTimer;
    private readonly DispatcherQueueTimer _animationTimer;
    private readonly Stopwatch _animationClock = new();

    private bool _isShowing;
    private bool _isPopupVisible;
    private bool _allowClose;
    private bool _isClosed;
    private DateTime _lastDismissedAtUtc = DateTime.MinValue;

    // Animation state: _baseX/_baseY is the resting position; the slide animates an offset below
    // it in physical pixels. _current* is where the last frame left off, so an animation that
    // interrupts another starts from there instead of snapping.
    private bool _isHiding;
    private int _baseX;
    private int _baseY;
    private int _fromOffset;
    private int _toOffset;
    private double _fromOpacity;
    private double _toOpacity;
    private double _durationMs = ShowDurationMs;
    private int _currentOffset;
    private double _currentOpacity;

    public TrayFlyoutWindow()
    {
        InitializeComponent();
        _hwnd = (HWND)WindowNative.GetWindowHandle(this);
        _page = new FlyoutPage(this);
        PageHost.Content = _page;

        SystemBackdrop = _backdrop;
        ApplyShellTheme();
        _uiSettings.ColorValuesChanged += OnColorValuesChanged;

        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        ConfigureWindowChrome();

        Activated += OnWindowActivated;
        AppWindow.Closing += OnAppWindowClosing;

        _hideTimer = DispatcherQueue.CreateTimer();
        _hideTimer.Interval = TimeSpan.FromMilliseconds(150);
        _hideTimer.IsRepeating = false;
        _hideTimer.Tick += HideTimer_Tick;

        _closeTimer = DispatcherQueue.CreateTimer();
        _closeTimer.Interval = CloseDelay;
        _closeTimer.IsRepeating = false;
        _closeTimer.Tick += CloseTimer_Tick;

        _animationTimer = DispatcherQueue.CreateTimer();
        _animationTimer.Interval = TimeSpan.FromMilliseconds(15);
        _animationTimer.IsRepeating = true;
        _animationTimer.Tick += AnimationTimer_Tick;
    }

    public bool IsPopupVisible => _isPopupVisible;

    private FrameworkElement CurrentView => (FrameworkElement)PageHost.Content;

    /// <summary>Shows the popup, or hides it if already visible (tray-icon click behavior).</summary>
    public void Toggle()
    {
        if (_isPopupVisible)
        {
            HidePopup();
            return;
        }

        if (DateTime.UtcNow - _lastDismissedAtUtc < RecentDismissWindow)
        {
            return;
        }

        ReturnHomeIfStale();
        ShowPopup();
    }

    /// <summary>After a while hidden, the next open starts from the printer list again.</summary>
    public void ReturnHomeIfStale()
    {
        if (!_isPopupVisible && DateTime.UtcNow - _lastDismissedAtUtc > KeepPageFor)
        {
            GoHome();
        }
    }

    /// <summary>Drills into a page, keeping the current one to go back to.</summary>
    public void Navigate(FrameworkElement view)
    {
        FrameworkElement current = CurrentView;
        (current as IFlyoutView)?.OnHidden();
        _backStack.Push(current);
        PageHost.Content = view;
        if (_isPopupVisible)
        {
            (view as IFlyoutView)?.OnShown();
        }
    }

    /// <summary>Goes back one page; false when already on the printer list.</summary>
    public bool GoBack()
    {
        if (_backStack.Count == 0)
        {
            return false;
        }

        FrameworkElement leaving = CurrentView;
        (leaving as IFlyoutView)?.OnHidden();
        (leaving as IDisposable)?.Dispose();
        PageHost.Content = _backStack.Pop();
        if (_isPopupVisible)
        {
            (CurrentView as IFlyoutView)?.OnShown();
        }

        return true;
    }

    public void GoHome()
    {
        if (PopToHome() && _isPopupVisible)
        {
            _page.OnShown();
        }
    }

    public void ShowSettings() => ShowPage<SettingsView>(() => new SettingsView(this));

    public void ShowDiscover() => ShowPage<DiscoverView>(() => new DiscoverView(this));

    public void ShowAddPrinter() => ShowPage<EditPrinterView>(() => new EditPrinterView(this));

    public void ShowPrinter(string printerId)
    {
        PopToHome();
        Navigate(new PrinterDetailView(this, printerId));
    }

    /// <summary>Opens <typeparamref name="T"/> straight from the printer list, unless it's already showing.</summary>
    private void ShowPage<T>(Func<T> create)
        where T : FrameworkElement
    {
        if (CurrentView is not T)
        {
            PopToHome();
            Navigate(create());
        }
    }

    /// <summary>
    /// Drops every page above the printer list without showing the ones in between. False when
    /// already home. The caller decides whether to show.
    /// </summary>
    private bool PopToHome()
    {
        if (_backStack.Count == 0)
        {
            return false;
        }

        FrameworkElement leaving = CurrentView;
        (leaving as IFlyoutView)?.OnHidden();
        (leaving as IDisposable)?.Dispose();
        while (_backStack.Count > 1)
        {
            (_backStack.Pop() as IDisposable)?.Dispose(); // Already hidden when it was navigated away from.
        }

        PageHost.Content = _backStack.Pop();
        return true;
    }

    public void ShowPopup()
    {
        // Position before showing so the window never flashes at a stale location.
        WindowPlacementService.PositionNearTray(this, PopupWidth, PopupHeight);
        _baseX = AppWindow.Position.X;
        _baseY = AppWindow.Position.Y;

        _hideTimer.Stop();
        _closeTimer.Stop();
        _isShowing = true;
        _isPopupVisible = true;

        // Re-read here too: not every taskbar setting change raises ColorValuesChanged.
        ApplyShellTheme();
        PlayShowAnimation();

        this.Show();
        Activate();
        PInvoke.SetForegroundWindow(_hwnd);

        App.Current.OnFlyoutShown();
        (CurrentView as IFlyoutView)?.OnShown();
    }

    public void HidePopup()
    {
        _hideTimer.Stop();
        _isShowing = false;

        if (!_isPopupVisible)
        {
            return;
        }

        _isPopupVisible = false;
        _lastDismissedAtUtc = DateTime.UtcNow;
        (CurrentView as IFlyoutView)?.OnHidden();
        App.Current.OnFlyoutHidden();
        PlayHideAnimation();

        _closeTimer.Stop();
        _closeTimer.Start();
    }

    /// <summary>
    /// Closes the window for real: stops its timers and unsubscribes from app-lifetime events so
    /// nothing keeps the window alive.
    /// </summary>
    public void CloseWindow()
    {
        if (_isClosed)
        {
            return;
        }

        _isClosed = true;
        _hideTimer.Stop();
        _closeTimer.Stop();
        _animationTimer.Stop();
        GoHome();
        _page.OnHidden();
        _page.Dispose();

        _uiSettings.ColorValuesChanged -= OnColorValuesChanged;
        Activated -= OnWindowActivated;
        AppWindow.Closing -= OnAppWindowClosing;

        // A native timer holds its Tick delegate, the delegate holds this window, and the window
        // holds the timer: a cycle through native code that the GC can't see through.
        _hideTimer.Tick -= HideTimer_Tick;
        _closeTimer.Tick -= CloseTimer_Tick;
        _animationTimer.Tick -= AnimationTimer_Tick;

        // Disconnect the backdrop now so its acrylic controller (which holds this window as its
        // target) is disposed, and drop the page tree, before the window goes.
        SystemBackdrop = null;
        PageHost.Content = null;

        // WinUIEx's WindowManager subscribes to AppWindow events and its own DispatcherQueueTimer
        // and unsubscribes only when disposed, which WindowEx never does. Without this, every
        // closed flyout stays alive with its whole XAML tree, about 15 MB each, for the life of
        // the process: a plain Window is collected after Close, a WindowEx isn't.
        WindowManager.Get(this).Dispose();

        _allowClose = true;
        Close();
    }

    private void CloseIfStillHidden()
    {
        if (!_isPopupVisible)
        {
            CloseWindow();
        }
    }

    private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        // Alt+F4 hides like any other dismissal.
        if (!_allowClose)
        {
            args.Cancel = true;
            HidePopup();
        }
    }

    // Raised off the UI thread when the accent color or light/dark mode changes.
    private void OnColorValuesChanged(UISettings sender, object args) => DispatcherQueue.TryEnqueue(ApplyShellTheme);

    /// <summary>Matches the taskbar's mode and accent tint, like the shell's own flyouts.</summary>
    private void ApplyShellTheme()
    {
        bool isLight = SystemThemeService.IsLight;
        PopupRoot.RequestedTheme = isLight ? ElementTheme.Light : ElementTheme.Dark;
        _backdrop.Update(isLight, SystemThemeService.ShowsAccentColor ? _uiSettings.GetColorValue(UIColorType.AccentDark2) : null);
    }

    private void PlayShowAnimation()
    {
        _isHiding = false;
        _durationMs = ShowDurationMs;
        _fromOffset = ScaleForDpi(ShowSlideDistance);
        _toOffset = 0;
        _fromOpacity = 0;
        _toOpacity = 1;

        ApplyFrame(_fromOffset, 0);
        _animationClock.Restart();
        _animationTimer.Start();
    }

    private void PlayHideAnimation()
    {
        _isHiding = true;
        _durationMs = HideDurationMs;
        _fromOffset = _currentOffset;
        _toOffset = ScaleForDpi(HideSlideDistance);
        _fromOpacity = _currentOpacity;
        _toOpacity = 0;

        _animationClock.Restart();
        _animationTimer.Start();
    }

    private void HideTimer_Tick(DispatcherQueueTimer sender, object args) => HideIfFocusLeftWindow();

    private void CloseTimer_Tick(DispatcherQueueTimer sender, object args) => CloseIfStillHidden();

    private void AnimationTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        double progress = Math.Min(1, _animationClock.Elapsed.TotalMilliseconds / _durationMs);

        // Cubic ease-out on the way in, ease-in on the way out; linear fade.
        double eased = _isHiding ? progress * progress * progress : 1 - Math.Pow(1 - progress, 3);
        int offset = _fromOffset + (int)Math.Round((_toOffset - _fromOffset) * eased);
        double opacity = _fromOpacity + ((_toOpacity - _fromOpacity) * progress);
        ApplyFrame(offset, opacity);

        if (progress < 1)
        {
            return;
        }

        _animationTimer.Stop();
        _animationClock.Reset();
        if (_isHiding)
        {
            this.Hide();
        }
    }

    private void ApplyFrame(int offset, double opacity)
    {
        _currentOffset = offset;
        _currentOpacity = opacity;

        byte alpha = (byte)Math.Clamp(Math.Round(opacity * 255), 0, 255);
        _ = PInvoke.SetLayeredWindowAttributes(_hwnd, new COLORREF(0), alpha, LAYERED_WINDOW_ATTRIBUTES_FLAGS.LWA_ALPHA);
        AppWindow.Move(new PointInt32(_baseX, _baseY + offset));
    }

    private int ScaleForDpi(int logical)
    {
        uint dpi = PInvoke.GetDpiForWindow(_hwnd);
        return (int)Math.Round(logical * (dpi == 0 ? 96 : dpi) / 96.0);
    }

    private void ConfigureWindowChrome()
    {
        OverlappedPresenter presenter = OverlappedPresenter.Create();
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        presenter.IsResizable = false;
        presenter.IsAlwaysOnTop = true;
        presenter.SetBorderAndTitleBar(true, false);
        AppWindow.SetPresenter(presenter);
        AppWindow.IsShownInSwitchers = false;

        unsafe
        {
            DWM_WINDOW_CORNER_PREFERENCE corner = DWM_WINDOW_CORNER_PREFERENCE.DWMWCP_ROUND;
            _ = PInvoke.DwmSetWindowAttribute(_hwnd, DWMWINDOWATTRIBUTE.DWMWA_WINDOW_CORNER_PREFERENCE, &corner, sizeof(DWM_WINDOW_CORNER_PREFERENCE));
        }

        // WS_EX_LAYERED enables the fade; WS_EX_TOOLWINDOW keeps it off the taskbar and Alt-Tab.
        int exStyle = PInvoke.GetWindowLong(_hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE);
        _ = PInvoke.SetWindowLong(_hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE, exStyle | WS_EX_LAYERED | WS_EX_TOOLWINDOW);
    }

    private void OnWindowActivated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState != WindowActivationState.Deactivated)
        {
            _isShowing = false;
            _hideTimer.Stop();
            return;
        }

        if (_isShowing || !_isPopupVisible)
        {
            return;
        }

        // Debounce: focus bounces briefly while XAML popups (tooltips, menus, dropdowns) open.
        _hideTimer.Stop();
        _hideTimer.Start();
    }

    private void HideIfFocusLeftWindow()
    {
        _hideTimer.Stop();
        if (_isPopupVisible && !ShouldRemainVisible(PInvoke.GetForegroundWindow()))
        {
            HidePopup();
        }
    }

    private bool ShouldRemainVisible(HWND foreground)
    {
        if (foreground == HWND.Null)
        {
            return false;
        }

        if (foreground == _hwnd || PInvoke.GetAncestor(foreground, GET_ANCESTOR_FLAGS.GA_ROOT) == _hwnd)
        {
            return true;
        }

        // WinUI flyouts and ComboBox dropdowns open in their own popup HWNDs; classic menus use #32768.
        Span<char> buffer = stackalloc char[64];
        int length = PInvoke.GetClassName(foreground, buffer);
        if (length <= 0)
        {
            return false;
        }

        ReadOnlySpan<char> className = buffer[..length];
        return className.SequenceEqual("Xaml_WindowedPopupClass") || className.SequenceEqual("#32768");
    }

    private void Escape_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        if (!GoBack())
        {
            HidePopup();
        }
    }

    private void GoBack_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = GoBack();
    }

    private void PopupRoot_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (e.GetCurrentPoint(PopupRoot).Properties.IsXButton1Pressed)
        {
            e.Handled = GoBack();
        }
    }
}
