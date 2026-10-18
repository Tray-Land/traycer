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
/// Draws the dynamic tray icons as ARGB pixels: a percentage over a progress bar, pause bars,
/// round badges, and frames of text scrolling across a plate. Shapes are signed-distance fields
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
    /// Scroll positions in one pass of a marquee <paramref name="textWidth"/> wide: the text enters
    /// at the right edge and leaves at the left, so a pass is its width plus one icon of bare plate.
    /// </summary>
    public static int MarqueeFrames(int size, int textWidth) => textWidth + size;

    /// <summary>A rounded square plate with the text scrolled to <paramref name="position"/> (0 to <see cref="MarqueeFrames"/>-1), clipped to the plate.</summary>
    /// <param name="text">Coverage (0-255) for a strip <paramref name="textWidth"/> wide and <paramref name="size"/> tall.</param>
    public static uint[] Marquee(int size, ReadOnlySpan<byte> text, int textWidth, int position, uint background, uint foreground)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(text.Length, textWidth * size, nameof(text));

        uint[] pixels = new uint[size * size];
        double radius = size * 0.2;
        int offset = position - size; // Column of the strip under the icon's left edge.
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
                double ink = column >= 0 && column < textWidth ? text[(y * textWidth) + column] / 255.0 : 0;
                pixels[(y * size) + x] = Over(0, Over(background, foreground, ink), plate);
            }
        }

        return pixels;
    }

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
