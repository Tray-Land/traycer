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
    [DataRow(16)]
    [DataRow(24)]
    public void Marquee_ScrollsTextRightToLeftAcrossARoundedPlate(int size)
    {
        // A one-column strip, fully inked: easy to follow across the frames.
        byte[] text = [.. Enumerable.Repeat((byte)255, size)];
        uint plate = TrayPalette.Dark.Success;
        uint ink = TrayPalette.Dark.OnSuccess;
        int middle = size / 2;

        Assert.AreEqual(size + 1, TrayIconPainter.MarqueeFrames(size, 1), "A pass is the text plus one icon of bare plate.");

        uint[] first = TrayIconPainter.Marquee(size, text, 1, 0, plate, ink);
        Assert.IsTrue(first.All(p => p == 0 || (p & 0xFFFFFF) == (plate & 0xFFFFFF)), "The pass starts on bare plate.");
        Assert.AreEqual(0u, At(first, size, 0, 0), "Rounded corners are transparent.");
        Assert.AreEqual(plate, At(first, size, 0, middle), "The plate fills the icon edge to edge.");

        uint[] halfway = TrayIconPainter.Marquee(size, text, 1, middle, plate, ink);
        Assert.AreEqual(ink, At(halfway, size, size - middle, middle), "The column entered from the right.");
        Assert.AreEqual(plate, At(halfway, size, size - middle - 1, middle));

        uint[] last = TrayIconPainter.Marquee(size, text, 1, size, plate, ink);
        Assert.AreEqual(ink, At(last, size, 0, middle), "And leaves at the left.");
        Assert.AreEqual(0u, At(last, size, 0, 0), "Text is clipped to the plate.");
    }

    [TestMethod]
    [DataRow(16)]
    [DataRow(24)]
    public void Heating_DrawsAHotendOverAnAmberBar(int size)
    {
        uint[] pixels = TrayIconPainter.Heating(size, 0.5, TrayPalette.Dark);
        int barRow = size - (TrayIconPainter.BarHeight(size) / 2) - 1;

        Assert.AreEqual(TrayPalette.Dark.Caution, At(pixels, size, size / 4, barRow), "Warmed so far, in amber.");
        Assert.AreEqual(TrayPalette.Dark.Track >> 24, (uint)Alpha(At(pixels, size, (size * 3) / 4, barRow)), "Still to warm: the track.");

        int textHeight = TrayIconPainter.TextHeight(size);
        Assert.AreEqual(TrayPalette.Dark.Foreground, At(pixels, size, size / 2, textHeight / 4), "The heater block.");
        Assert.AreEqual(TrayPalette.Dark.Foreground, At(pixels, size, size / 2, (textHeight * 3) / 4), "The nozzle under it.");
        Assert.AreEqual(0u, At(pixels, size, size / 4, (textHeight * 3) / 4), "The nozzle is narrower than the block.");
    }

    [TestMethod]
    [DataRow(20)]
    [DataRow(32)]
    public void Marquee_WithHotend_LaysTextDownUnderTheNozzle(int size)
    {
        byte[] text = [.. Enumerable.Repeat((byte)255, size)];
        uint plate = TrayPalette.Dark.Success;
        uint ink = TrayPalette.Dark.OnSuccess;
        int middle = size / 2;
        int frames = TrayIconPainter.MarqueeFrames(size, 1, hotend: true);
        Assert.IsLessThan(TrayIconPainter.MarqueeFrames(size, 1), frames, "The text starts under the nozzle, not at the edge.");

        // On the first step the column appears under the nozzle; nothing to its right but plate.
        uint[] first = TrayIconPainter.Marquee(size, text, 1, 1, plate, ink, hotend: true);
        int entered = Enumerable.Range(0, size).Single(x => At(first, size, x, middle) == ink);
        Assert.IsTrue(entered > size / 2 && entered < size - 2, "The text comes out from under the head, short of the right edge.");
        Assert.IsTrue(Enumerable.Range(entered + 1, size - entered - 1).All(x => (At(first, size, x, middle) & 0xFFFFFF) == (plate & 0xFFFFFF)));
        Assert.AreEqual(ink, At(first, size, entered, 1), "The head's block sits at the top, above where the text appears.");

        uint[] last = TrayIconPainter.Marquee(size, text, 1, frames - 1, plate, ink, hotend: true);
        Assert.AreEqual(ink, At(last, size, 0, middle), "And it still leaves at the left.");
        Assert.IsFalse(TrayIconPainter.HotendFits(16), "Too small to read at 100% scale.");
    }

    [TestMethod]
    public void SlideOut_DropsThePlateAndFadesIt()
    {
        const int size = 16;
        uint[] tile = TrayIconPainter.Badge(size, BadgeSymbol.Check, TrayPalette.Dark.Success, TrayPalette.Dark.OnSuccess);

        CollectionAssert.AreEqual(tile, TrayIconPainter.SlideOut(tile, size, 0));

        uint[] half = TrayIconPainter.SlideOut(tile, size, 0.5);
        int shift = size / 4; // Eases in: halfway through time is a quarter of the way down.
        Assert.IsTrue(half.Take(size * shift).All(p => p == 0), "The top is empty once it starts to drop.");
        uint moved = At(half, size, size / 2, (size / 2) + shift);
        Assert.AreEqual(At(tile, size, size / 2, size / 2) & 0xFFFFFF, moved & 0xFFFFFF, "Same color, lower down.");
        Assert.AreEqual(128u, (uint)Alpha(moved), 1, "Half faded.");

        Assert.IsTrue(TrayIconPainter.SlideOut(tile, size, 1).All(p => p == 0), "Gone.");
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
