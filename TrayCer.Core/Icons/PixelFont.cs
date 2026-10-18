namespace TrayCer.Core.Icons;

/// <summary>A 3×5 pixel font for digits and "-", used when the system font can't be rendered.</summary>
internal static class PixelFont
{
    private const int GlyphWidth = 3;
    private const int GlyphHeight = 5;

    // One byte per row, the low three bits left to right.
    private static readonly byte[][] Digits =
    [
        [0b111, 0b101, 0b101, 0b101, 0b111],
        [0b010, 0b110, 0b010, 0b010, 0b111],
        [0b111, 0b001, 0b111, 0b100, 0b111],
        [0b111, 0b001, 0b111, 0b001, 0b111],
        [0b101, 0b101, 0b111, 0b001, 0b001],
        [0b111, 0b100, 0b111, 0b001, 0b111],
        [0b111, 0b100, 0b111, 0b101, 0b111],
        [0b111, 0b001, 0b001, 0b001, 0b001],
        [0b111, 0b101, 0b111, 0b101, 0b111],
        [0b111, 0b101, 0b111, 0b001, 0b111],
    ];

    private static readonly byte[] Dash = [0b000, 0b000, 0b111, 0b000, 0b000];

    /// <summary>Coverage for a width × height box with the text centered at the largest whole scale that fits.</summary>
    public static byte[] Render(string text, int width, int height)
    {
        byte[] coverage = new byte[width * height];
        byte[][] glyphs = [.. text.Select(c => c is >= '0' and <= '9' ? Digits[c - '0'] : Dash)];
        if (glyphs.Length == 0)
        {
            return coverage;
        }

        int columns = (glyphs.Length * (GlyphWidth + 1)) - 1;
        int scale = Math.Max(1, Math.Min(width / columns, height / GlyphHeight));
        int left = (width - (columns * scale)) / 2;
        int top = (height - (GlyphHeight * scale)) / 2;

        for (int g = 0; g < glyphs.Length; g++)
        {
            int glyphLeft = left + (g * (GlyphWidth + 1) * scale);
            for (int row = 0; row < GlyphHeight; row++)
            {
                for (int column = 0; column < GlyphWidth; column++)
                {
                    if ((glyphs[g][row] & (1 << (GlyphWidth - 1 - column))) == 0)
                    {
                        continue;
                    }

                    for (int y = top + (row * scale); y < top + ((row + 1) * scale); y++)
                    {
                        for (int x = glyphLeft + (column * scale); x < glyphLeft + ((column + 1) * scale); x++)
                        {
                            if (x >= 0 && x < width && y >= 0 && y < height)
                            {
                                coverage[(y * width) + x] = 255;
                            }
                        }
                    }
                }
            }
        }

        return coverage;
    }
}
