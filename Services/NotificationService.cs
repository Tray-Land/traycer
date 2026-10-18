using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace TrayCer.Services;

/// <summary>
/// Windows App SDK toast notifications for print events. Each printer has its own tag, so a newer
/// alert for a printer replaces its previous one in Action Center instead of piling up.
/// </summary>
internal static class NotificationService
{
    private const string Group = "prints";

    private static bool _registered;

    /// <summary>Raised on a background thread when a notification is clicked (to open the flyout).</summary>
    public static event EventHandler? Opened;

    public static void Register()
    {
        try
        {
            AppNotificationManager.Default.NotificationInvoked += OnNotificationInvoked;
            AppNotificationManager.Default.Register();
            _registered = true;
        }
        catch (Exception ex)
        {
            // Alerts are a nice-to-have; the tray icon still shows when a print is done.
            System.Diagnostics.Debug.WriteLine($"App notifications unavailable: {ex.Message}");
        }
    }

    public static void Unregister()
    {
        if (_registered)
        {
            AppNotificationManager.Default.Unregister();
            _registered = false;
        }
    }

    public static void Show(string printerId, string title, string text)
    {
        if (!_registered)
        {
            return;
        }

        try
        {
            AppNotification notification = new AppNotificationBuilder()
                .AddText(title)
                .AddText(text)
                .BuildNotification();
            notification.Tag = printerId;
            notification.Group = Group;
            notification.ExpiresOnReboot = true;
            AppNotificationManager.Default.Show(notification);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Notification failed: {ex.Message}");
        }
    }

    /// <summary>Clears every alert once the user has opened the flyout and seen the news.</summary>
    public static void ClearAll()
    {
        if (_registered)
        {
            _ = AppNotificationManager.Default.RemoveByGroupAsync(Group);
        }
    }

    private static void OnNotificationInvoked(AppNotificationManager sender, AppNotificationActivatedEventArgs args) =>
        Opened?.Invoke(null, EventArgs.Empty);
}
