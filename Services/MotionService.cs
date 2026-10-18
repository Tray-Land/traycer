using Windows.UI.ViewManagement;

namespace TrayCer.Services;

/// <summary>
/// The Windows "Animation effects" setting (Accessibility > Visual effects). With it off, the tray
/// icon holds still, the flyout appears and disappears in place, and nothing inside it moves.
/// </summary>
internal static class MotionService
{
    private static readonly UISettings Settings = new();

    static MotionService()
    {
        // Windows 10 1809 and 1909 have the setting but no event: there, a change shows on the next redraw.
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
        {
            Settings.AnimationsEnabledChanged += (_, _) => Changed?.Invoke(null, EventArgs.Empty);
        }
    }

    /// <summary>Raised on a background thread when the setting changes. Only app-lifetime objects should listen.</summary>
    public static event EventHandler? Changed;

    public static bool AnimationsEnabled => Settings.AnimationsEnabled;
}
