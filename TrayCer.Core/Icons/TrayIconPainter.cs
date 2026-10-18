namespace TrayCer.Core.Icons;

/// <summary>
/// Tray colors as 0xAARRGGBB. The taskbar's theme picks the set: tray glyphs sit directly on the
/// taskbar, and badges follow the InfoBadge colors so they read like Windows' own.
/// </summary>
public sealed record TrayPalette(uint Foreground, uint Track, uint Caution, uint Success, uint OnSuccess, uint Warning, uint OnWarning, uint Critical, uint OnCritical)
{
    public static readonly TrayPalette Dark = new(
        Foreground: 0xFFFFFFFF, Track: 0x5CFFFFFF, Caution: 0xFFFCE100,
        Success: 0xFF6CCB5F, OnSuccess: 0xFF000000,
        Warning: 0xFFFCE100, OnWarning: 0xFF000000,
        Critical: 0xFFFF99A4, OnCritical: 0xFF000000);

    public static readonly TrayPalette Light = new(
        Foreground: 0xE6000000, Track: 0x45000000, Caution: 0xFF9D5D00,
        Success: 0xFF0F7B0F, OnSuccess: 0xFFFFFFFF,
        Warning: 0xFF9D5D00, OnWarning: 0xFFFFFFFF,
        Critical: 0xFFC42B1C, OnCritical: 0xFFFFFFFF);

    public static TrayPalette For(bool lightTaskbar) => lightTaskbar ? Light : Dark;
}

public enum BadgeSymbol
{
    Check,
    Exclamation,
}

/// <summary>
/// Draws the dynamic tray icons as ARGB pixels: a percentage over a progress bar, a hotend over a
/// warming bar, pause bars, round badges, and frames of text scrolling across a plate under a
/// print head. Shapes are signed-distance fields
/// sampled at pixel centers, which anti-aliases them without a graphics library. Text arrives as
/// coverage rendered by the app in the system font; a built-in pixel font stands in for digits
/// when that fails (and in tests).
/// </summary>
public static class TrayIconPainter
{
    public static int BarHeight(int size) => Math.Max(2, (int)Math.Round(size / 7.0));

    /// <summary>Height of the text area above the bar; the app renders digits into size × this.</summary>
    public static int TextHeight(int size) => size - BarHeight(size) - Gap(size);

    /// <param name="text">Coverage (0-255) for a size × <see cref="TextHeight"/> box, or empty to draw <paramref name="fallbackText"/> in the pixel font.</param>
    public static uint[] Progress(int size, double? progress, ReadOnlySpan<byte> text, string fallbackText, TrayPalette palette)
    {
        uint[] pixels = new uint[size * size];
        int textHeight = TextHeight(size);
        byte[] coverage = text.Length == size * textHeight ? text.ToArray() : PixelFont.Render(fallbackText, size, textHeight);
        for (int i = 0; i < coverage.Length; i++)
        {
            pixels[i] = Over(pixels[i], palette.Foreground, coverage[i] / 255.0);
        }

        DrawBar(pixels, size, progress, palette.Foreground, palette.Track);
        return pixels;
    }

    /// <summary>A hotend above a bar that fills in amber as the heaters reach their targets.</summary>
    public static uint[] Heating(int size, double? heat, TrayPalette palette)
    {
        uint[] pixels = new uint[size * size];
        int textHeight = TextHeight(size);
        Fill(pixels, size, palette.Foreground, (x, y) => Hotend(x, y, size / 2.0, textHeight * 0.06, textHeight * 0.9, size * 0.56));
        DrawBar(pixels, size, heat, palette.Caution, palette.Track);
        return pixels;
    }

    public static uint[] Paused(int size, double? progress, TrayPalette palette)
    {
        uint[] pixels = new uint[size * size];
        int textHeight = TextHeight(size);
        double barWidth = Math.Max(2, size * 0.19);
        double gap = Math.Max(2, size * 0.17);
        double top = Math.Max(1, textHeight * 0.12);
        double bottom = textHeight - Math.Max(1, textHeight * 0.08);
        double left = (size - (2 * barWidth) - gap) / 2;
        double radius = Math.Min(barWidth / 2, size / 12.0);
        Fill(pixels, size, palette.Caution, (x, y) => Math.Min(
            RoundedRect(x, y, left, top, left + barWidth, bottom, radius),
            RoundedRect(x, y, left + barWidth + gap, top, left + (2 * barWidth) + gap, bottom, radius)));

        DrawBar(pixels, size, progress, palette.Caution, palette.Track);
        return pixels;
    }

    public static uint[] Badge(int size, BadgeSymbol symbol, uint background, uint foreground)
    {
        uint[] pixels = new uint[size * size];
        double center = size / 2.0;
        double radius = (size / 2.0) - 0.25;
        Fill(pixels, size, background, (x, y) => Circle(x, y, center, center, radius));

        if (symbol == BadgeSymbol.Check)
        {
            double half = Math.Max(0.85, size * 0.075);
            Fill(pixels, size, foreground, (x, y) => Math.Min(
                Segment(x, y, size * 0.28, size * 0.52, size * 0.44, size * 0.68) - half,
                Segment(x, y, size * 0.44, size * 0.68, size * 0.73, size * 0.35) - half));
        }
        else
        {
            double half = Math.Max(0.85, size * 0.08);
            Fill(pixels, size, foreground, (x, y) => Math.Min(
                Segment(x, y, center, size * 0.25, center, size * 0.56) - half,
                Circle(x, y, center, size * 0.75, Math.Max(1, size * 0.09))));
        }

        return pixels;
    }

    /// <summary>How tall a capital plus a descender stands in a marquee: a little under the icon, so the plate frames the text.</summary>
    public static int MarqueeLineHeight(int size) => Math.Max(5, (int)Math.Round(size * 0.8));

    /// <summary>
    /// Whether a marquee this size has room for a print head over its text. The head fits in the
    /// strip above the capitals, which is too thin to read below 20 pixels (125% scale).
    /// </summary>
    public static bool HotendFits(int size) => size >= 20;

    /// <summary>
    /// Scroll positions in one pass of a marquee <paramref name="textWidth"/> wide. The text enters
    /// at the right edge, or under the print head, and leaves at the left, so a pass is its width
    /// plus the width it travels across.
    /// </summary>
    public static int MarqueeFrames(int size, int textWidth, bool hotend = false) => textWidth + Entry(size, hotend);

    /// <summary>
    /// A rounded square plate with the text scrolled to <paramref name="position"/> (0 to
    /// <see cref="MarqueeFrames"/>-1), clipped to the plate. With <paramref name="hotend"/>, a print
    /// head sits over the right of the plate and the text comes out from under it, like a belt
    /// printer laying it down: nothing shows to the right of the nozzle.
    /// </summary>
    /// <param name="text">Coverage (0-255) for a strip <paramref name="textWidth"/> wide and <paramref name="size"/> tall.</param>
    public static uint[] Marquee(int size, ReadOnlySpan<byte> text, int textWidth, int position, uint background, uint foreground, bool hotend = false)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(text.Length, textWidth * size, nameof(text));

        uint[] pixels = new uint[size * size];
        double radius = size * 0.2;
        int entry = Entry(size, hotend);
        int offset = position - entry; // Column of the strip under the icon's left edge.
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                double plate = Math.Clamp(0.5 - RoundedRect(x + 0.5, y + 0.5, 0, 0, size, size, radius), 0, 1);
                if (plate <= 0)
                {
                    continue;
                }

                int column = offset + x;
                double ink = x < entry && column >= 0 && column < textWidth ? text[(y * textWidth) + column] / 255.0 : 0;
                if (hotend)
                {
                    ink = Math.Max(ink, Math.Clamp(0.5 - MarqueeHotend(x + 0.5, y + 0.5, size), 0, 1));
                }

                pixels[(y * size) + x] = Over(0, Over(background, foreground, ink), plate);
            }
        }

        return pixels;
    }

    /// <summary>
    /// A frame of <paramref name="pixels"/> sliding down out of the icon and fading, from
    /// <paramref name="t"/> = 0 (in place) to 1 (gone): the plate pulled out of the printer.
    /// </summary>
    public static uint[] SlideOut(ReadOnlySpan<uint> pixels, int size, double t)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(pixels.Length, size * size, nameof(pixels));

        t = Math.Clamp(t, 0, 1);
        int shift = (int)Math.Round(size * t * t); // Eases in: it starts slowly, then drops away.
        uint[] frame = new uint[size * size];
        for (int y = shift; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                frame[(y * size) + x] = Over(0, pixels[((y - shift) * size) + x], 1 - t);
            }
        }

        return frame;
    }

    /// <summary>The first column the marquee text can't reach: the icon's right edge, or just past the nozzle.</summary>
    private static int Entry(int size, bool hotend) => hotend ? (int)Math.Floor(NozzleX(size)) + 1 : size;

    private static double NozzleX(int size) => size * 0.72;

    /// <summary>The marquee's print head, in the strip above the capitals, its tip just touching them.</summary>
    private static double MarqueeHotend(double px, double py, int size) =>
        Hotend(px, py, NozzleX(size), size * 0.02, size * 0.25, size * 0.36);

    private static int Gap(int size) => Math.Max(1, size / 16);

    /// <summary>A full-width capsule along the bottom: the track, then the filled part on top.</summary>
    private static void DrawBar(uint[] pixels, int size, double? progress, uint fill, uint track)
    {
        double height = BarHeight(size);
        double top = size - height;
        double radius = height / 2;
        Fill(pixels, size, track, (x, y) => RoundedRect(x, y, 0, top, size, size, radius));

        if (progress is { } p && p > 0)
        {
            double width = Math.Max(height, Math.Clamp(p, 0, 1) * size);
            Fill(pixels, size, fill, (x, y) => RoundedRect(x, y, 0, top, width, size, radius));
        }
    }

    private static void Fill(uint[] pixels, int size, uint color, Func<double, double, double> distance)
    {
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                double coverage = Math.Clamp(0.5 - distance(x + 0.5, y + 0.5), 0, 1);
                if (coverage > 0)
                {
                    int i = (y * size) + x;
                    pixels[i] = Over(pixels[i], color, coverage);
                }
            }
        }
    }

    private static double RoundedRect(double px, double py, double x0, double y0, double x1, double y1, double radius)
    {
        double cx = (x0 + x1) / 2;
        double cy = (y0 + y1) / 2;
        double qx = Math.Abs(px - cx) - (((x1 - x0) / 2) - radius);
        double qy = Math.Abs(py - cy) - (((y1 - y0) / 2) - radius);
        double outside = Math.Sqrt((Math.Max(qx, 0) * Math.Max(qx, 0)) + (Math.Max(qy, 0) * Math.Max(qy, 0)));
        return outside + Math.Min(Math.Max(qx, qy), 0) - radius;
    }

    /// <summary>
    /// A hotend seen from the front, centered on <paramref name="cx"/>: a heater block
    /// <paramref name="width"/> wide over a nozzle that narrows to its tip at <paramref name="tipY"/>.
    /// </summary>
    private static double Hotend(double px, double py, double cx, double top, double tipY, double width)
    {
        double blockBottom = top + ((tipY - top) * 0.55);
        double radius = Math.Min(width, tipY - top) * 0.1;
        double block = RoundedRect(px, py, cx - (width / 2), top, cx + (width / 2), blockBottom, radius);

        // The nozzle starts half a pixel inside the block, so no seam shows between them.
        double nozzle = Trapezoid(px, py, cx, blockBottom - 0.5, tipY, width * 0.32, width * 0.1);
        return Math.Min(block, nozzle);
    }

    /// <summary>A trapezoid centered on <paramref name="cx"/>, <paramref name="topHalf"/> wide either side at the top and <paramref name="bottomHalf"/> at the bottom.</summary>
    private static double Trapezoid(double px, double py, double cx, double top, double bottom, double topHalf, double bottomHalf)
    {
        // After Inigo Quilez's 2D distance functions, with y running down.
        double he = (bottom - top) / 2;
        double x = Math.Abs(px - cx);
        double y = py - ((top + bottom) / 2);
        double k2x = bottomHalf - topHalf;
        double k2y = 2 * he;
        double cax = x - Math.Min(x, y < 0 ? topHalf : bottomHalf);
        double cay = Math.Abs(y) - he;
        double t = Math.Clamp((((bottomHalf - x) * k2x) + ((he - y) * k2y)) / ((k2x * k2x) + (k2y * k2y)), 0, 1);
        double cbx = x - bottomHalf + (k2x * t);
        double cby = y - he + (k2y * t);
        double sign = cbx < 0 && cay < 0 ? -1 : 1;
        return sign * Math.Sqrt(Math.Min((cax * cax) + (cay * cay), (cbx * cbx) + (cby * cby)));
    }

    private static double Circle(double px, double py, double cx, double cy, double radius) =>
        Math.Sqrt(((px - cx) * (px - cx)) + ((py - cy) * (py - cy))) - radius;

    private static double Segment(double px, double py, double ax, double ay, double bx, double by)
    {
        double dx = bx - ax;
        double dy = by - ay;
        double t = Math.Clamp((((px - ax) * dx) + ((py - ay) * dy)) / ((dx * dx) + (dy * dy)), 0, 1);
        double ex = px - (ax + (t * dx));
        double ey = py - (ay + (t * dy));
        return Math.Sqrt((ex * ex) + (ey * ey));
    }

    /// <summary>Straight-alpha source-over of <paramref name="top"/> at <paramref name="coverage"/> onto <paramref name="bottom"/>.</summary>
    internal static uint Over(uint bottom, uint top, double coverage)
    {
        double sa = ((top >> 24) / 255.0) * coverage;
        double da = (bottom >> 24) / 255.0;
        double oa = sa + (da * (1 - sa));
        if (oa <= 0)
        {
            return 0;
        }

        uint result = (uint)Math.Round(oa * 255) << 24;
        for (int shift = 0; shift < 24; shift += 8)
        {
            double s = (top >> shift) & 0xFF;
            double d = (bottom >> shift) & 0xFF;
            double c = ((s * sa) + (d * da * (1 - sa))) / oa;
            result |= (uint)Math.Clamp(Math.Round(c), 0, 255) << shift;
        }

        return result;
    }
}
