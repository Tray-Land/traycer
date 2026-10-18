using System.Globalization;
using Microsoft.UI;
using TrayCer.Core.Icons;
using TrayCer.Core.Presentation;
using Windows.Win32;
using Windows.Win32.UI.WindowsAndMessaging;
using WinUIEx;

namespace TrayCer.Services;

/// <summary>
/// Puts a <see cref="TrayIndicator"/> on the tray icon: the app icon file when there's nothing to
/// report, otherwise a generated icon. Owns the generated handle: the shell copies the image when
/// the icon is set, so the previous one is freed right after.
/// </summary>
internal sealed class TrayIconRenderer
{
    private const uint IconFormatVersion = 0x00030000; // Required by CreateIconFromResourceEx.

    private HICON _icon;
    private string? _shownKey;

    public void Show(TrayIcon trayIcon, TrayIndicator indicator, bool lightTaskbar, string appIconPath)
    {
        int size = PInvoke.GetSystemMetricsForDpi(SYSTEM_METRICS_INDEX.SM_CXSMICON, PInvoke.GetDpiForSystem());
        string text = indicator.Progress is { } p ? Math.Clamp((int)Math.Floor(p * 100), 0, 99).ToString(CultureInfo.InvariantCulture) : "--";
        string key = $"{indicator.Glyph}|{(indicator.Glyph is TrayGlyph.Progress or TrayGlyph.Paused ? text : "")}|{lightTaskbar}|{size}";
        if (key == _shownKey)
        {
            return;
        }

        _shownKey = key;
        if (indicator.Glyph == TrayGlyph.App)
        {
            trayIcon.SetIcon(appIconPath);
            Release();
            return;
        }

        TrayPalette palette = TrayPalette.For(lightTaskbar);
        uint[] pixels = indicator.Glyph switch
        {
            TrayGlyph.Progress => TrayIconPainter.Progress(size, indicator.Progress, TrayFont.Render(text, size, TrayIconPainter.TextHeight(size)) ?? [], text, palette),
            TrayGlyph.Paused => TrayIconPainter.Paused(size, indicator.Progress, palette),
            TrayGlyph.Done => TrayIconPainter.Badge(size, BadgeSymbol.Check, palette.Success, palette.OnSuccess),
            TrayGlyph.Warning => TrayIconPainter.Badge(size, BadgeSymbol.Exclamation, palette.Warning, palette.OnWarning),
            _ => TrayIconPainter.Badge(size, BadgeSymbol.Exclamation, palette.Critical, palette.OnCritical),
        };

        byte[] ico = IcoEncoder.Encode(pixels, size);
        HICON icon;
        nint handle;
        unsafe
        {
            fixed (byte* bits = ico)
            {
                icon = PInvoke.CreateIconFromResourceEx(
                    bits + IcoEncoder.ImageOffset,
                    (uint)(ico.Length - IcoEncoder.ImageOffset),
                    true,
                    IconFormatVersion,
                    size,
                    size,
                    IMAGE_FLAGS.LR_DEFAULTCOLOR);
            }

            handle = (nint)icon.Value;
        }

        if (handle == 0)
        {
            _shownKey = null;
            return;
        }

        trayIcon.SetIcon(Win32Interop.GetIconIdFromIcon(handle));
        Release();
        _icon = icon;
    }

    /// <summary>Forgets what's shown, so the next <see cref="Show"/> redraws (after a theme or DPI change).</summary>
    public void Invalidate() => _shownKey = null;

    /// <summary>Frees the generated icon; call after the tray icon has been switched back to the file icon.</summary>
    public void Release()
    {
        if (!_icon.IsNull)
        {
            PInvoke.DestroyIcon(_icon);
            _icon = default;
        }
    }
}
