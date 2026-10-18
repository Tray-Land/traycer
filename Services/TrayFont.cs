using System.Runtime.InteropServices;

namespace TrayCer.Services;

/// <summary>
/// Renders tray icon text in the small optical size of the Windows 11 system font through GDI, as
/// per-pixel coverage for <see cref="Core.Icons.TrayIconPainter"/>. Render returns null when
/// nothing draws, and the painter falls back to its pixel font.
/// </summary>
internal static partial class TrayFont
{
    private const string FaceName = "Segoe UI Variable Small";

    private const uint DefaultCharset = 1;
    private const uint AntialiasedQuality = 4;
    private const int Transparent = 1;
    private const int SemiboldWeight = 600;

    /// <summary>Draws <paramref name="text"/> as large as fits a width × height box, centered on its ink.</summary>
    public static byte[]? Render(string text, int width, int height)
    {
        // Draw once at a nominal size to measure the ink, then again scaled so the ink fills the box.
        int canvas = Math.Max(width, height) * 3;
        int em = height;
        for (int pass = 0; pass < 2; pass++)
        {
            byte[] ink = Draw(text, canvas, em);
            if (!TryInkBounds(ink, canvas, out int left, out int top, out int inkWidth, out int inkHeight))
            {
                return null;
            }

            if (pass == 1)
            {
                return Crop(ink, canvas, left, top, inkWidth, inkHeight, width, height);
            }

            em = Math.Max(1, (int)(em * Math.Min((double)width / inkWidth, (double)height / inkHeight)));
        }

        return null;
    }

    private static unsafe byte[] Draw(string text, int canvas, int em)
    {
        nint dc = CreateCompatibleDC(0);
        BitmapInfoHeader header = new()
        {
            Size = (uint)sizeof(BitmapInfoHeader),
            Width = canvas,
            Height = -canvas, // Top-down.
            Planes = 1,
            BitCount = 32,
        };
        nint bitmap = CreateDIBSection(dc, ref header, 0, out nint bits, 0, 0);
        nint oldBitmap = SelectObject(dc, bitmap);
        nint font = CreateFont(-em, 0, 0, 0, SemiboldWeight, 0, 0, 0, DefaultCharset, 0, 0, AntialiasedQuality, 0, FaceName);
        nint oldFont = SelectObject(dc, font);

        Span<uint> pixels = new((void*)bits, canvas * canvas);
        pixels.Clear();
        SetTextColor(dc, 0x00FFFFFF);
        SetBkMode(dc, Transparent);
        TextOut(dc, canvas / 6, canvas / 6, text, text.Length);
        GdiFlush();

        // White on black, so any channel is the coverage; take the brightest in case of subpixel output.
        byte[] ink = new byte[canvas * canvas];
        for (int i = 0; i < ink.Length; i++)
        {
            uint p = pixels[i];
            ink[i] = (byte)Math.Max(p & 0xFF, Math.Max((p >> 8) & 0xFF, (p >> 16) & 0xFF));
        }

        SelectObject(dc, oldFont);
        DeleteObject(font);
        SelectObject(dc, oldBitmap);
        DeleteObject(bitmap);
        DeleteDC(dc);
        return ink;
    }

    private static bool TryInkBounds(byte[] ink, int canvas, out int left, out int top, out int width, out int height)
    {
        int minX = canvas, minY = canvas, maxX = -1, maxY = -1;
        for (int y = 0; y < canvas; y++)
        {
            for (int x = 0; x < canvas; x++)
            {
                if (ink[(y * canvas) + x] != 0)
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

    private static byte[] Crop(byte[] ink, int canvas, int left, int top, int inkWidth, int inkHeight, int width, int height)
    {
        byte[] result = new byte[width * height];
        int offsetX = (width - inkWidth) / 2;
        int offsetY = (height - inkHeight) / 2;
        for (int y = 0; y < inkHeight; y++)
        {
            for (int x = 0; x < inkWidth; x++)
            {
                int dx = x + offsetX;
                int dy = y + offsetY;
                if (dx >= 0 && dx < width && dy >= 0 && dy < height)
                {
                    result[(dy * width) + dx] = ink[((top + y) * canvas) + left + x];
                }
            }
        }

        return result;
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
