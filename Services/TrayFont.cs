using System.Runtime.InteropServices;

namespace TrayCer.Services;

/// <summary>
/// Renders tray icon text in the small optical size of the Windows 11 system font through GDI, as
/// per-pixel coverage for <see cref="Core.Icons.TrayIconPainter"/>. Both methods return null when
/// nothing draws: the painter falls back to its pixel font, the finished marquee to a check badge.
/// </summary>
internal static partial class TrayFont
{
    private const string FaceName = "Segoe UI Variable Small";

    // A marquee line is sized on a capital and a descender, so every text gets the same size and
    // baseline whatever its letters, and centered on the capital, where the eye puts its middle.
    private const string SizeReference = "Hg";
    private const string CenterReference = "H";

    private const uint DefaultCharset = 1;
    private const uint AntialiasedQuality = 4;
    private const int Transparent = 1;
    private const int SemiboldWeight = 600;

    /// <summary>Draws <paramref name="text"/> as large as fits a width × height box, centered on its ink.</summary>
    public static byte[]? Render(string text, int width, int height)
    {
        // Draw once at a nominal size to measure the ink, then again scaled so the ink fills the box.
        int em = height;
        for (int pass = 0; pass < 2; pass++)
        {
            Canvas canvas = Draw(text, em);
            if (!canvas.TryInkBounds(out int left, out int top, out int inkWidth, out int inkHeight))
            {
                return null;
            }

            if (pass == 1)
            {
                return canvas.Crop(left - ((width - inkWidth) / 2), top - ((height - inkHeight) / 2), width, height);
            }

            em = Math.Max(1, (int)(em * Math.Min((double)width / inkWidth, (double)height / inkHeight)));
        }

        return null;
    }

    /// <summary>
    /// Draws <paramref name="text"/> on one line in a strip <paramref name="height"/> pixels tall,
    /// sized so a capital and a descender span <paramref name="lineHeight"/>, with the capital
    /// height centered, and cropped to the text's ink width.
    /// </summary>
    public static (byte[] Coverage, int Width)? RenderLine(string text, int height, int lineHeight)
    {
        if (string.IsNullOrWhiteSpace(text) || !Draw(SizeReference, height).TryInkBounds(out _, out _, out _, out int probeHeight))
        {
            return null;
        }

        int em = Math.Max(1, (int)Math.Round(height * (double)lineHeight / probeHeight));
        Canvas capital = Draw(CenterReference, em);
        Canvas line = Draw(text, em);
        if (!capital.TryInkBounds(out _, out int capTop, out _, out int capHeight) || !line.TryInkBounds(out int left, out _, out int width, out _))
        {
            return null;
        }

        // Both are drawn at the same origin, so they share a baseline; the capital places it in the strip.
        return (line.Crop(left, capTop - ((height - capHeight) / 2), width, height), width);
    }

    /// <summary>Draws white text on black, with a margin of one em all round for overhangs and accents.</summary>
    private static unsafe Canvas Draw(string text, int em)
    {
        nint dc = CreateCompatibleDC(0);
        nint font = CreateFont(-em, 0, 0, 0, SemiboldWeight, 0, 0, 0, DefaultCharset, 0, 0, AntialiasedQuality, 0, FaceName);
        nint oldFont = SelectObject(dc, font);
        _ = GetTextExtentPoint32(dc, text, text.Length, out Size extent);

        int width = extent.Cx + (em * 2);
        int height = em * 3;
        BitmapInfoHeader header = new()
        {
            Size = (uint)sizeof(BitmapInfoHeader),
            Width = width,
            Height = -height, // Top-down.
            Planes = 1,
            BitCount = 32,
        };
        nint bitmap = CreateDIBSection(dc, ref header, 0, out nint bits, 0, 0);
        nint oldBitmap = SelectObject(dc, bitmap);

        Span<uint> pixels = new((void*)bits, width * height);
        pixels.Clear();
        SetTextColor(dc, 0x00FFFFFF);
        SetBkMode(dc, Transparent);
        TextOut(dc, em, em, text, text.Length);
        GdiFlush();

        // White on black, so any channel is the coverage; take the brightest in case of subpixel output.
        byte[] ink = new byte[width * height];
        for (int i = 0; i < ink.Length; i++)
        {
            uint p = pixels[i];
            ink[i] = (byte)Math.Max(p & 0xFF, Math.Max((p >> 8) & 0xFF, (p >> 16) & 0xFF));
        }

        SelectObject(dc, oldBitmap);
        DeleteObject(bitmap);
        SelectObject(dc, oldFont);
        DeleteObject(font);
        DeleteDC(dc);
        return new Canvas(ink, width, height);
    }

    private readonly record struct Canvas(byte[] Ink, int Width, int Height)
    {
        public bool TryInkBounds(out int left, out int top, out int width, out int height)
        {
            int minX = Width, minY = Height, maxX = -1, maxY = -1;
            for (int y = 0; y < Height; y++)
            {
                for (int x = 0; x < Width; x++)
                {
                    if (Ink[(y * Width) + x] != 0)
                    {
                        minX = Math.Min(minX, x);
                        maxX = Math.Max(maxX, x);
                        minY = Math.Min(minY, y);
                        maxY = Math.Max(maxY, y);
                    }
                }
            }

            left = minX;
            top = minY;
            width = maxX - minX + 1;
            height = maxY - minY + 1;
            return maxX >= 0;
        }

        /// <summary>The width × height region at (left, top); anything outside the canvas is blank.</summary>
        public byte[] Crop(int left, int top, int width, int height)
        {
            byte[] result = new byte[width * height];
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int sx = left + x;
                    int sy = top + y;
                    if (sx >= 0 && sx < Width && sy >= 0 && sy < Height)
                    {
                        result[(y * width) + x] = Ink[(sy * Width) + sx];
                    }
                }
            }

            return result;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ClrUsed;
        public uint ClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Size
    {
        public int Cx;
        public int Cy;
    }

    [LibraryImport("gdi32.dll")]
    private static partial nint CreateCompatibleDC(nint dc);

    [LibraryImport("gdi32.dll")]
    private static partial nint CreateDIBSection(nint dc, ref BitmapInfoHeader info, uint usage, out nint bits, nint section, uint offset);

    [LibraryImport("gdi32.dll", EntryPoint = "CreateFontW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CreateFont(
        int height,
        int width,
        int escapement,
        int orientation,
        int weight,
        uint italic,
        uint underline,
        uint strikeOut,
        uint charSet,
        uint outPrecision,
        uint clipPrecision,
        uint quality,
        uint pitchAndFamily,
        string faceName);

    [LibraryImport("gdi32.dll", EntryPoint = "GetTextExtentPoint32W", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetTextExtentPoint32(nint dc, string text, int length, out Size size);

    [LibraryImport("gdi32.dll")]
    private static partial nint SelectObject(nint dc, nint obj);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteObject(nint obj);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteDC(nint dc);

    [LibraryImport("gdi32.dll")]
    private static partial uint SetTextColor(nint dc, uint color);

    [LibraryImport("gdi32.dll")]
    private static partial int SetBkMode(nint dc, int mode);

    [LibraryImport("gdi32.dll", EntryPoint = "TextOutW", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TextOut(nint dc, int x, int y, string text, int length);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GdiFlush();
}
