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
/// A finished print scrolls its text across a green plate, like tiny-atc's marquee, coming out from
/// under a print head when the icon is big enough to show one: the text is rendered once per
/// change, and each frame only slices it, so a frame is a small copy, an ICO header and an icon
/// swap. When the last finished print is marked complete, the plate slides out in a few frames.
/// Frames run only while there's something to animate, never while the session is locked or the
/// display is off (<see cref="Away"/>), and not at all when Windows' animation effects are off.
/// </remarks>
internal sealed class TrayIconRenderer : IDisposable
{
    private const uint IconFormatVersion = 0x00030000; // Required by CreateIconFromResourceEx.

    private const int SlideFrames = 4;

    private static readonly TimeSpan FrameInterval = TimeSpan.FromMilliseconds(55);

    private readonly TrayIcon _trayIcon;
    private readonly DispatcherQueueTimer _frameTimer;
    private HICON _icon;
    private string? _shownKey;
    private TrayGlyph _shownGlyph;
    private uint[]? _shownPixels;
    private Marquee? _marquee;
    private int _position;
    private Slide? _slide;
    private bool _away;

    public TrayIconRenderer(TrayIcon trayIcon, DispatcherQueue dispatcher)
    {
        _trayIcon = trayIcon;
        _frameTimer = dispatcher.CreateTimer();
        _frameTimer.Interval = FrameInterval;
        _frameTimer.IsRepeating = true;
        _frameTimer.Tick += FrameTimer_Tick;
    }

    /// <summary>True while nobody can see the tray: the marquee holds its frame until they're back.</summary>
    public bool Away
    {
        get => _away;
        set
        {
            _away = value;
            if (value)
            {
                _frameTimer.Stop();
                FinishSlide();
            }
            else if (_marquee is not null)
            {
                _frameTimer.Start();
            }
        }
    }

    public void Show(TrayIndicator indicator, bool lightTaskbar, string appIconPath)
    {
        int size = PInvoke.GetSystemMetricsForDpi(SYSTEM_METRICS_INDEX.SM_CXSMICON, PInvoke.GetDpiForSystem());
        bool animate = MotionService.AnimationsEnabled;
        string key = $"{indicator.Glyph}|{(indicator.Glyph is TrayGlyph.Progress or TrayGlyph.Heating or TrayGlyph.Paused ? Digits(indicator.Progress) : "")}|{indicator.Marquee}|{lightTaskbar}|{size}|{animate}";
        if (key == _shownKey)
        {
            return;
        }

        // The last finished print was marked complete (or the next print began): pull the plate out
        // before showing what's next. Not when the tile is only giving way to something more urgent.
        bool cleared = _shownGlyph == TrayGlyph.Done && indicator.Glyph != TrayGlyph.Done && !indicator.HasFinished;
        _shownKey = key;
        _shownGlyph = indicator.Glyph;
        StopAnimation();
        if (cleared && animate && !_away && _shownPixels is { } plate && plate.Length == size * size)
        {
            _slide = new Slide(plate, size, () => Draw(indicator, lightTaskbar, appIconPath, size, animate));
            NextFrame();
            _frameTimer.Start();
            return;
        }

        Draw(indicator, lightTaskbar, appIconPath, size, animate);
    }

    /// <summary>Forgets what's shown, so the next <see cref="Show"/> redraws (after a theme, DPI or motion setting change).</summary>
    public void Invalidate() => _shownKey = null;

    /// <summary>Stops any animation and frees the generated icon; call before the tray icon goes away.</summary>
    public void Dispose()
    {
        StopAnimation();
        _frameTimer.Tick -= FrameTimer_Tick;
        Release();
    }

    private void Draw(TrayIndicator indicator, bool lightTaskbar, string appIconPath, int size, bool animate)
    {
        if (indicator.Glyph == TrayGlyph.App)
        {
            _trayIcon.SetIcon(appIconPath);
            Release();
            _shownPixels = null;
            return;
        }

        TrayPalette palette = TrayPalette.For(lightTaskbar);
        if (indicator.Glyph == TrayGlyph.Done && animate && indicator.Marquee is { } marquee && StartMarquee(marquee, size, palette))
        {
            return;
        }

        string text = Digits(indicator.Progress);
        uint[] pixels = indicator.Glyph switch
        {
            TrayGlyph.Progress => TrayIconPainter.Progress(size, indicator.Progress, TrayFont.Render(text, size, TrayIconPainter.TextHeight(size)) ?? [], text, palette),
            TrayGlyph.Heating => TrayIconPainter.Heating(size, indicator.Progress, palette),
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

    /// <summary>The percentage the icon has room for: two digits, or dashes when there's none.</summary>
    private static string Digits(double? progress) =>
        progress is { } p ? Math.Clamp((int)Math.Floor(p * 100), 0, 99).ToString(CultureInfo.InvariantCulture) : "--";

    /// <summary>Renders the text once and starts scrolling it; false when it can't be drawn.</summary>
    private bool StartMarquee(string text, int size, TrayPalette palette)
    {
        if (TrayFont.RenderLine(text, size, TrayIconPainter.MarqueeLineHeight(size)) is not { } line)
        {
            return false;
        }

        _marquee = new Marquee(line.Coverage, line.Width, size, palette, TrayIconPainter.HotendFits(size));
        _position = 0;
        NextFrame();
        if (!_away)
        {
            _frameTimer.Start();
        }

        return true;
    }

    private void StopAnimation()
    {
        _frameTimer.Stop();
        _marquee = null;
        _slide = null;
    }

    private void FrameTimer_Tick(DispatcherQueueTimer sender, object args) => NextFrame();

    private void NextFrame()
    {
        if (_slide is { } slide)
        {
            if (slide.Frame >= SlideFrames)
            {
                FinishSlide();
                return;
            }

            slide.Frame++;
            SetPixels(TrayIconPainter.SlideOut(slide.Plate, slide.Size, slide.Frame / (double)(SlideFrames + 1)), slide.Size);
            return;
        }

        if (_marquee is not { } m)
        {
            _frameTimer.Stop();
            return;
        }

        if (_position >= TrayIconPainter.MarqueeFrames(m.Size, m.Width, m.Hotend))
        {
            _position = 0;
        }

        SetPixels(TrayIconPainter.Marquee(m.Size, m.Text, m.Width, _position, m.Palette.Success, m.Palette.OnSuccess, m.Hotend), m.Size);
        _position += Math.Max(1, m.Size / 16); // Same apparent speed at every display scale.
    }

    /// <summary>Ends a slide early or on time, and shows what it was making way for.</summary>
    private void FinishSlide()
    {
        if (_slide is { } slide)
        {
            _slide = null;
            _frameTimer.Stop();
            slide.Then();
        }
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
        _shownPixels = pixels;
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
    private sealed record Marquee(byte[] Text, int Width, int Size, TrayPalette Palette, bool Hotend);

    /// <summary>The finished tile on its way out, and what to show once it's gone.</summary>
    private sealed class Slide(uint[] plate, int size, Action then)
    {
        public uint[] Plate { get; } = plate;

        public int Size { get; } = size;

        public Action Then { get; } = then;

        public int Frame { get; set; }
    }
}
