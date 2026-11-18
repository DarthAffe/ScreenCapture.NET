using System.Runtime.Versioning;
using HPPH;
using Vortice.DXGI;

namespace ScreenCapture.NET.Tests;

/// <summary>
/// <see cref="CursorShape"/>: how the pointer shapes of Desktop Duplication are read and drawn into zones (also downscaled ones).
/// </summary>
[TestClass]
[OSCondition(OperatingSystems.Windows)]
[SupportedOSPlatform("windows")]
public sealed class CursorShapeTests
{
    #region Methods

    private static readonly ColorBGRA BACKGROUND = new(10, 20, 30, 42);

    private static ColorBGRA[] CreateZone(int width, int height, ColorBGRA color) => Enumerable.Repeat(color, width * height).ToArray();

    // A color (or masked color) shape of one row, BGRA per pixel.
    private static CursorShape CreateRow(PointerShapeType type, params ColorBGRA[] pixels)
    {
        byte[] data = [.. pixels.SelectMany(p => new[] { p.B, p.G, p.R, p.A })];
        return CursorShape.Create(type, pixels.Length, 1, data.Length, data);
    }

    // An opaque black color shape of width x height pixels.
    private static CursorShape CreateBlack(int width, int height)
    {
        byte[] data = new byte[width * height * 4];
        for (int i = 3; i < data.Length; i += 4)
            data[i] = 255;

        return CursorShape.Create(PointerShapeType.Color, width, height, width * 4, data);
    }

    private static ColorBGRA[] Draw(CursorShape shape, int x, int y, CaptureRegion region, int downscaleLevel = 0, ColorBGRA? background = null)
    {
        (int width, int height) = PixelHelper.GetDownscaledSize(region.Width, region.Height, 1 << downscaleLevel);
        ColorBGRA[] zone = CreateZone(width, height, background ?? BACKGROUND);
        shape.Draw(zone, region, width, height, downscaleLevel, x, y);

        return zone;
    }

    [TestMethod]
    public void ColorShapeIsAlphaBlended()
    {
        CursorShape shape = CreateRow(PointerShapeType.Color, new ColorBGRA(200, 100, 0, 255), new ColorBGRA(200, 100, 0, 0), new ColorBGRA(255, 255, 255, 128));

        ColorBGRA[] zone = Draw(shape, 0, 0, new CaptureRegion(0, 0, 4, 1));

        Assert.AreEqual(new ColorBGRA(200, 100, 0, 42), zone[0], "opaque");
        Assert.AreEqual(BACKGROUND, zone[1], "transparent");
        // 255 * 128 / 255 + value * 127 / 255
        Assert.AreEqual(new ColorBGRA(133, 138, 143, 42), zone[2], "half transparent");
        Assert.AreEqual(BACKGROUND, zone[3], "not covered");
    }

    [TestMethod]
    public void MonochromeShapeAppliesBothMasks()
    {
        // One row of 4 pixels: AND 1100, XOR 0101 → keep, invert, black, white. The reported height includes both masks.
        byte[] data = [0b1100_0000, 0b0101_0000];
        CursorShape shape = CursorShape.Create(PointerShapeType.Monochrome, 4, 2, 1, data);

        Assert.AreEqual(1, shape.Height);

        ColorBGRA[] zone = Draw(shape, 0, 0, new CaptureRegion(0, 0, 4, 1));

        CollectionAssert.AreEqual(new[]
        {
            BACKGROUND,
            new ColorBGRA(245, 235, 225, 42),
            new ColorBGRA(0, 0, 0, 42),
            new ColorBGRA(255, 255, 255, 42)
        }, zone);
    }

    [TestMethod]
    public void MonochromeShapeUsesThePitch()
    {
        // 9 pixels per row need 2 bytes, the pitch is 4. Only the 9th pixel of the AND mask is 0, the XOR mask is 0: it's black.
        byte[] data = [0xFF, 0x00, 0xAA, 0xAA, 0x00, 0x00, 0xAA, 0xAA];
        CursorShape shape = CursorShape.Create(PointerShapeType.Monochrome, 9, 2, 4, data);

        ColorBGRA[] zone = Draw(shape, 0, 0, new CaptureRegion(0, 0, 9, 1));

        Assert.IsTrue(zone.Take(8).All(p => p == BACKGROUND));
        Assert.AreEqual(new ColorBGRA(0, 0, 0, 42), zone[8]);
    }

    [TestMethod]
    public void MaskedColorShapeReplacesOrXors()
    {
        CursorShape shape = CreateRow(PointerShapeType.MaskedColor,
                                      new ColorBGRA(1, 2, 3, 0),       // replaced
                                      new ColorBGRA(255, 255, 255, 255), // inverted
                                      new ColorBGRA(0, 0, 0, 255),     // kept
                                      new ColorBGRA(255, 0, 200, 255)); // per channel: inverted, kept, inverted (approximation of XOR 200)

        ColorBGRA[] zone = Draw(shape, 0, 0, new CaptureRegion(0, 0, 4, 1));

        CollectionAssert.AreEqual(new[]
        {
            new ColorBGRA(1, 2, 3, 42),
            new ColorBGRA(245, 235, 225, 42),
            BACKGROUND,
            new ColorBGRA(245, 20, 225, 42)
        }, zone);
    }

    [TestMethod]
    public void ShapeIsPlacedRelativeToTheRegion()
    {
        // The region starts at (10, 20) on the display: the pointer at (11, 22) covers zone pixels (1, 2) and (2, 2).
        ColorBGRA[] zone = Draw(CreateBlack(2, 1), 11, 22, new CaptureRegion(10, 20, 4, 3));

        for (int i = 0; i < zone.Length; i++)
        {
            bool covered = i is 9 or 10;
            Assert.AreEqual(covered ? new ColorBGRA(0, 0, 0, 42) : BACKGROUND, zone[i], $"pixel {i}");
        }
    }

    [TestMethod]
    public void ShapeIsClippedToTheZone()
    {
        // A 3x3 pointer at (-1, -1) relative to a 2x2 region covers the whole zone; the rest of it lies outside.
        ColorBGRA[] topLeft = Draw(CreateBlack(3, 3), 4, 4, new CaptureRegion(5, 5, 2, 2));
        Assert.IsTrue(topLeft.All(p => p == new ColorBGRA(0, 0, 0, 42)), "top left");

        // Overlapping the bottom right corner only.
        ColorBGRA[] bottomRight = Draw(CreateBlack(3, 3), 6, 6, new CaptureRegion(5, 5, 2, 2));
        CollectionAssert.AreEqual(new[] { BACKGROUND, BACKGROUND, BACKGROUND, new ColorBGRA(0, 0, 0, 42) }, bottomRight, "bottom right");
    }

    [TestMethod]
    public void ShapeOutsideTheZoneChangesNothing()
    {
        CaptureRegion region = new(10, 10, 4, 4);
        CursorShape shape = CreateBlack(3, 3);

        foreach ((int x, int y) in new[] { (0, 0), (7, 10), (14, 10), (10, 14), (10, 7), (100, 100), (-50, -50) })
            Assert.IsTrue(Draw(shape, x, y, region).All(p => p == BACKGROUND), $"({x}, {y})");
    }

    [TestMethod]
    public void DownscaledZoneAveragesTheBlock()
    {
        // Level 1: 2x2 blocks. The black 3x1 pointer at (1, 1) covers one pixel of the first block (3 of 4 kept) and two of the second.
        ColorBGRA background = new(200, 100, 40, 255);
        ColorBGRA[] zone = Draw(CreateBlack(3, 1), 1, 1, new CaptureRegion(0, 0, 6, 2), downscaleLevel: 1, background);

        CollectionAssert.AreEqual(new[]
        {
            new ColorBGRA(150, 75, 30, 255),
            new ColorBGRA(100, 50, 20, 255),
            background
        }, zone);

        // Covering a whole block replaces it.
        ColorBGRA[] covered = Draw(CreateBlack(2, 2), 2, 0, new CaptureRegion(0, 0, 6, 2), downscaleLevel: 1, background);
        CollectionAssert.AreEqual(new[] { background, new ColorBGRA(0, 0, 0, 255), background }, covered);
    }

    [TestMethod]
    public void DownscaledZoneUsesBlocksFromTheRegionsTopLeft()
    {
        // Region at x = 3, level 1: blocks cover display columns 3-4 and 5-6. A 1x2 pointer at column 5 fills half of the second block.
        ColorBGRA background = new(200, 100, 40, 255);
        ColorBGRA[] zone = Draw(CreateBlack(1, 2), 5, 0, new CaptureRegion(3, 0, 4, 2), downscaleLevel: 1, background);

        CollectionAssert.AreEqual(new[] { background, new ColorBGRA(100, 50, 20, 255) }, zone);
    }

    [TestMethod]
    public void DownscaledZoneIgnoresIncompleteBlocks()
    {
        // 5 columns at level 1 give 2 zone pixels; the 5th column isn't part of a block, so a pointer there changes nothing.
        ColorBGRA[] zone = Draw(CreateBlack(1, 2), 4, 0, new CaptureRegion(0, 0, 5, 2), downscaleLevel: 1);

        Assert.HasCount(2, zone);
        Assert.IsTrue(zone.All(p => p == BACKGROUND));
    }

    [TestMethod]
    public void RegionSmallerThanABlockIsOneBlock()
    {
        // Level 2 (4x4 blocks) on a 2x2 region: one block of 4 pixels, the pointer covers 2 of them.
        ColorBGRA background = new(200, 100, 40, 255);
        ColorBGRA[] zone = Draw(CreateBlack(2, 1), 0, 0, new CaptureRegion(0, 0, 2, 2), downscaleLevel: 2, background);

        CollectionAssert.AreEqual(new[] { new ColorBGRA(100, 50, 20, 255) }, zone);
    }

    [TestMethod]
    public void InvertingShapeStaysInvertingWhenDownscaled()
    {
        // A 2x2 block of an inverting monochrome pointer over pixels averaging 100: the zone's pixel 100 becomes 155.
        byte[] data = [0b1100_0000, 0b1100_0000, 0b1100_0000, 0b1100_0000];
        CursorShape shape = CursorShape.Create(PointerShapeType.Monochrome, 2, 4, 1, data);

        ColorBGRA[] zone = Draw(shape, 0, 0, new CaptureRegion(0, 0, 2, 2), downscaleLevel: 1, new ColorBGRA(100, 100, 100, 255));

        CollectionAssert.AreEqual(new[] { new ColorBGRA(155, 155, 155, 255) }, zone);
    }

    [TestMethod]
    public void InvalidShapesThrow()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => CursorShape.Create((PointerShapeType)8, 1, 1, 4, new byte[4]), "unknown type");
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => CursorShape.Create(PointerShapeType.Color, 2, 1, 4, new byte[8]), "pitch too small");
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => CursorShape.Create(PointerShapeType.Color, -1, 1, 4, new byte[4]), "negative width");
        Assert.ThrowsExactly<ArgumentException>(() => CursorShape.Create(PointerShapeType.Color, 2, 2, 8, new byte[15]), "data too small");
        Assert.ThrowsExactly<ArgumentException>(() => CursorShape.Create(PointerShapeType.Monochrome, 8, 2, 1, new byte[1]), "masks too small");
    }

    #endregion
}
