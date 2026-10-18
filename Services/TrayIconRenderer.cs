using System.Globalization;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
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
/// <remarks>
/// A finished print scrolls its text across a green plate, like tiny-atc's marquee: the text is
/// rendered once per change, and each frame only slices it, so a frame is a small copy, an ICO
/// header and an icon swap. It animates only while a print waits to be marked complete.
/// </remarks>
internal sealed class TrayIconRenderer : IDisposable
{
    private const uint IconFormatVersion = 0x00030000; // Required by CreateIconFromResourceEx.

    private static readonly TimeSpan FrameInterval = TimeSpan.FromMilliseconds(55);

    private readonly TrayIcon _trayIcon;
    private readonly DispatcherQueueTimer _frameTimer;
    private HICON _icon;
    private string? _shownKey;
    private Marquee? _marquee;
    private int _position;

    public TrayIconRenderer(TrayIcon trayIcon, DispatcherQueue dispatcher)
    {
        _trayIcon = trayIcon;
        _frameTimer = dispatcher.CreateTimer();
        _frameTimer.Interval = FrameInterval;
        _frameTimer.IsRepeating = true;
        _frameTimer.Tick += FrameTimer_Tick;
    }

    public void Show(TrayIndicator indicator, bool lightTaskbar, string appIconPath)
    {
        int size = PInvoke.GetSystemMetricsForDpi(SYSTEM_METRICS_INDEX.SM_CXSMICON, PInvoke.GetDpiForSystem());
        string text = indicator.Progress is { } p ? Math.Clamp((int)Math.Floor(p * 100), 0, 99).ToString(CultureInfo.InvariantCulture) : "--";
        string key = $"{indicator.Glyph}|{(indicator.Glyph is TrayGlyph.Progress or TrayGlyph.Paused ? text : "")}|{indicator.Marquee}|{lightTaskbar}|{size}";
        if (key == _shownKey)
        {
            return;
        }

        _shownKey = key;
        StopMarquee();
        if (indicator.Glyph == TrayGlyph.App)
        {
            _trayIcon.SetIcon(appIconPath);
            Release();
            return;
        }

        TrayPalette palette = TrayPalette.For(lightTaskbar);
        if (indicator.Glyph == TrayGlyph.Done && indicator.Marquee is { } marquee && StartMarquee(marquee, size, palette))
        {
            return;
        }

        uint[] pixels = indicator.Glyph switch
        {
            TrayGlyph.Progress => TrayIconPainter.Progress(size, indicator.Progress, TrayFont.Render(text, size, TrayIconPainter.TextHeight(size)) ?? [], text, palette),
            TrayGlyph.Paused => TrayIconPainter.Paused(size, indicator.Progress, palette),
            TrayGlyph.Done => TrayIconPainter.Badge(size, BadgeSymbol.Check, palette.Success, palette.OnSuccess),
            TrayGlyph.Warning => TrayIconPainter.Badge(size, BadgeSymbol.Exclamation, palette.Warning, palette.OnWarning),
            _ => TrayIconPainter.Badge(size, BadgeSymbol.Exclamation, palette.Critical, palette.OnCritical),
        };

        if (!SetPixels(pixels, size))
        {
            _shownKey = null;
        }
    }

    /// <summary>Forgets what's shown, so the next <see cref="Show"/> redraws (after a theme or DPI change).</summary>
    public void Invalidate() => _shownKey = null;

    /// <summary>Stops the marquee and frees the generated icon; call before the tray icon goes away.</summary>
    public void Dispose()
    {
        StopMarquee();
        _frameTimer.Tick -= FrameTimer_Tick;
        Release();
    }

    /// <summary>Renders the text once and starts scrolling it; false when it can't be drawn.</summary>
    private bool StartMarquee(string text, int size, TrayPalette palette)
    {
        if (TrayFont.RenderLine(text, size, TrayIconPainter.MarqueeLineHeight(size)) is not { } line)
        {
            return false;
        }

        _marquee = new Marquee(line.Coverage, line.Width, size, palette);
        _position = 0;
        NextFrame();
        _frameTimer.Start();
        return true;
    }

    private void StopMarquee()
    {
        _frameTimer.Stop();
        _marquee = null;
    }

    private void FrameTimer_Tick(DispatcherQueueTimer sender, object args) => NextFrame();

    private void NextFrame()
    {
        if (_marquee is not { } m)
        {
            _frameTimer.Stop();
            return;
        }

        if (_position >= TrayIconPainter.MarqueeFrames(m.Size, m.Width))
        {
            _position = 0;
        }

        SetPixels(TrayIconPainter.Marquee(m.Size, m.Text, m.Width, _position, m.Palette.Success, m.Palette.OnSuccess), m.Size);
        _position += Math.Max(1, m.Size / 16); // Same apparent speed at every display scale.
    }

    private bool SetPixels(uint[] pixels, int size)
    {
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
            return false;
        }

        _trayIcon.SetIcon(Win32Interop.GetIconIdFromIcon(handle));
        Release();
        _icon = icon;
        return true;
    }

    private void Release()
    {
        if (!_icon.IsNull)
        {
            PInvoke.DestroyIcon(_icon);
            _icon = default;
        }
    }

    /// <summary>The marquee's text as a strip of coverage one icon tall, and the colors to draw it in.</summary>
    private sealed record Marquee(byte[] Text, int Width, int Size, TrayPalette Palette);
}
