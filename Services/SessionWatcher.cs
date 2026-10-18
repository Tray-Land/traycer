using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Power;
using Windows.Win32.UI.WindowsAndMessaging;
using WinUIEx.Messaging;

namespace TrayCer.Services;

/// <summary>
/// Knows when nobody can see the tray: while the session is locked or its display is off. The
/// finished marquee stops then, so a print that finishes overnight doesn't redraw the icon until
/// morning. Windows reports both to a window, so this keeps a message-only one; it never polls.
/// </summary>
internal sealed class SessionWatcher : IDisposable
{
    private const uint WM_WTSSESSION_CHANGE = 0x02B1;
    private const uint WM_POWERBROADCAST = 0x0218;
    private const nuint PBT_POWERSETTINGCHANGE = 0x8013;
    private const nuint WTS_SESSION_LOCK = 0x7;
    private const nuint WTS_SESSION_UNLOCK = 0x8;
    private const uint NOTIFY_FOR_THIS_SESSION = 0;
    private const int DisplayOff = 0; // GUID_SESSION_DISPLAY_STATUS data: 0 off, 1 on, 2 dimmed.

    private static readonly HWND HWND_MESSAGE = new(-3);

    private readonly HWND _window;
    private readonly WindowMessageMonitor _monitor;
    private readonly HPOWERNOTIFY _displayNotification;
    private readonly bool _sessionRegistered;
    private bool _locked;
    private bool _displayOff;

    public unsafe SessionWatcher()
    {
        // A STATIC control needs no window class of its own; message-only, it's never on screen.
        _window = PInvoke.CreateWindowEx(0, "Static", "Traycer session", 0, 0, 0, 0, 0, HWND_MESSAGE, null, null, null);
        _monitor = new WindowMessageMonitor((nint)_window.Value);
        _monitor.WindowMessageReceived += OnWindowMessage;
        _sessionRegistered = PInvoke.WTSRegisterSessionNotification(_window, NOTIFY_FOR_THIS_SESSION);

        // Windows answers right away with the display's current state, then with every change.
        Guid displayStatus = PInvoke.GUID_SESSION_DISPLAY_STATUS;
        _displayNotification = PInvoke.RegisterPowerSettingNotification(new HANDLE(_window.Value), &displayStatus, REGISTER_NOTIFICATION_FLAGS.DEVICE_NOTIFY_WINDOW_HANDLE);
    }

    /// <summary>Raised on the UI thread when <see cref="IsAway"/> changes.</summary>
    public event EventHandler? Changed;

    /// <summary>True while the session is locked or its display is off.</summary>
    public bool IsAway => _locked || _displayOff;

    public unsafe void Dispose()
    {
        if (_sessionRegistered)
        {
            PInvoke.WTSUnRegisterSessionNotification(_window);
        }

        if (!_displayNotification.IsNull)
        {
            PInvoke.UnregisterPowerSettingNotification(_displayNotification);
        }

        _monitor.WindowMessageReceived -= OnWindowMessage;
        _monitor.Dispose();
        PInvoke.DestroyWindow(_window);
    }

    private void OnWindowMessage(object? sender, WindowMessageEventArgs e)
    {
        bool wasAway = IsAway;
        switch (e.Message.MessageId)
        {
            case WM_WTSSESSION_CHANGE when e.Message.WParam is WTS_SESSION_LOCK or WTS_SESSION_UNLOCK:
                _locked = e.Message.WParam == WTS_SESSION_LOCK;
                break;

            case WM_POWERBROADCAST when e.Message.WParam == PBT_POWERSETTINGCHANGE && e.Message.LParam != 0:
                // POWERBROADCAST_SETTING: the setting's GUID, the data's length, then the data.
                if (Marshal.PtrToStructure<Guid>(e.Message.LParam) == PInvoke.GUID_SESSION_DISPLAY_STATUS)
                {
                    _displayOff = Marshal.ReadInt32(e.Message.LParam, 20) == DisplayOff;
                }

                e.Handled = true;
                e.Result = 1; // TRUE: handled.
                break;

            default:
                return;
        }

        if (IsAway != wasAway)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }
}
