using System.Buffers.Binary;
using TrayCer.Core.Icons;

namespace TrayCer.Tests;

[TestClass]
public sealed class IconTests
{
    private static uint At(uint[] pixels, int size, int x, int y) => pixels[(y * size) + x];

    private static byte Alpha(uint pixel) => (byte)(pixel >> 24);

    [TestMethod]
    [DataRow(16)]
    [DataRow(20)]
    [DataRow(24)]
    [DataRow(32)]
    public void Progress_FillsTheBarUpToTheProgress(int size)
    {
        uint[] pixels = TrayIconPainter.Progress(size, 0.5, [], "50", TrayPalette.Dark);
        int barRow = size - (TrayIconPainter.BarHeight(size) / 2) - 1;

        uint filled = At(pixels, size, size / 4, barRow);
        uint empty = At(pixels, size, (size * 3) / 4, barRow);
        Assert.AreEqual(0xFFFFFFFFu, filled, "Left of 50% is the solid foreground.");
        Assert.AreEqual(TrayPalette.Dark.Track >> 24, (uint)Alpha(empty), "Right of 50% is only the translucent track.");
    }

    [TestMethod]
    public void Progress_DrawsFallbackDigitsAboveTheBar()
    {
        const int size = 16;
        uint[] pixels = TrayIconPainter.Progress(size, 0.42, [], "42", TrayPalette.Light);
        int textRows = TrayIconPainter.TextHeight(size);

        int inked = pixels.Take(size * textRows).Count(p => Alpha(p) > 0);
        Assert.IsGreaterThan(20, inked, "The pixel font drew the digits.");
        Assert.AreEqual(0u, At(pixels, size, 0, 0), "Corners stay transparent.");
    }

    [TestMethod]
    public void Progress_UsesAppRenderedTextWhenGiven()
    {
        const int size = 16;
        byte[] text = new byte[size * TrayIconPainter.TextHeight(size)];
        text[(2 * size) + 3] = 255;

        uint[] pixels = TrayIconPainter.Progress(size, null, text, "--", TrayPalette.Dark);

        Assert.AreEqual(0xFFFFFFFFu, At(pixels, size, 3, 2));
        Assert.AreEqual(1, pixels.Take(size * TrayIconPainter.TextHeight(size)).Count(p => Alpha(p) > 0));
    }

    [TestMethod]
    public void Badge_IsARoundPlateWithASymbol()
    {
        const int size = 24;
        uint[] pixels = TrayIconPainter.Badge(size, BadgeSymbol.Check, TrayPalette.Light.Success, TrayPalette.Light.OnSuccess);

        Assert.AreEqual(0u, At(pixels, size, 0, 0), "Outside the circle is transparent.");
        Assert.AreEqual(TrayPalette.Light.Success, At(pixels, size, size / 2, 3), "Plate color near the top edge.");
        Assert.IsTrue(pixels.Any(p => p == TrayPalette.Light.OnSuccess), "The check mark is drawn.");
    }

    [TestMethod]
    public void Paused_DrawsTwoBarsInCaution()
    {
        const int size = 20;
        uint[] pixels = TrayIconPainter.Paused(size, 0.3, TrayPalette.Dark);
        int middle = TrayIconPainter.TextHeight(size) / 2;
        uint[] row = [.. Enumerable.Range(0, size).Select(x => At(pixels, size, x, middle))];

        int runs = 0;
        for (int x = 0; x < size; x++)
        {
            if (row[x] == TrayPalette.Dark.Caution && (x == 0 || row[x - 1] != TrayPalette.Dark.Caution))
            {
                runs++;
            }
        }

        Assert.AreEqual(2, runs);
    }

    [TestMethod]
    public void Ico_HasOneImageWithTheRightHeader()
    {
        const int size = 16;
        byte[] ico = IcoEncoder.Encode(new uint[size * size], size);

        Assert.AreEqual(1, BinaryPrimitives.ReadUInt16LittleEndian(ico.AsSpan(2)));
        Assert.AreEqual(1, BinaryPrimitives.ReadUInt16LittleEndian(ico.AsSpan(4)));
        Assert.AreEqual(size, ico[6]);
        Assert.AreEqual(IcoEncoder.ImageOffset, BinaryPrimitives.ReadInt32LittleEndian(ico.AsSpan(18)));
        Assert.AreEqual(size * 2, BinaryPrimitives.ReadInt32LittleEndian(ico.AsSpan(IcoEncoder.ImageOffset + 8)), "DIB height covers the AND mask.");
    }

    [TestMethod]
    public void Over_CompositesStraightAlpha()
    {
        Assert.AreEqual(0xFFFFFFFFu, TrayIconPainter.Over(0, 0xFFFFFFFF, 1));
        Assert.AreEqual(0u, TrayIconPainter.Over(0, 0xFFFFFFFF, 0));
        uint half = TrayIconPainter.Over(0xFF000000, 0xFFFFFFFF, 0.5);
        Assert.AreEqual(0xFFu, half >> 24);
        Assert.AreEqual(128u, half & 0xFF, 1);
    }
}
