using HPPH;

namespace ScreenCapture.NET.Tests;

/// <summary>
/// <see cref="CaptureZoneWriter.Write{TColor}"/>: which pixels of the frame end up in the zone, at which size.
/// The averaging itself is HPPH's: the frames consist of uniform blocks, so every average is exact.
/// </summary>
[TestClass]
public sealed class WriteCaptureZoneTests
{
    #region Methods

    // A frame of uniform blocks of blockSize x blockSize pixels, each with its own color.
    private static ColorARGB[] CreateFrame(int width, int height, int blockSize)
    {
        ColorARGB[] pixels = new ColorARGB[width * height];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                pixels[(y * width) + x] = BlockColor(x / blockSize, y / blockSize);

        return pixels;
    }

    private static ColorARGB BlockColor(int column, int row) => new(255, (byte)(column * 16), (byte)(row * 16), 42);

    private static ColorARGB[] Write(RefImage<ColorARGB> frame, CaptureZone<ColorARGB> zone)
    {
        ColorARGB[] buffer = new ColorARGB[zone.Width * zone.Height];
        CaptureZoneWriter.Write(frame, zone, buffer);

        return buffer;
    }

    [TestMethod]
    public void CopiesTheRegion()
    {
        using TestScreenCapture screenCapture = new();
        CaptureZone<ColorARGB> zone = screenCapture.RegisterCaptureZone(new CaptureRegion(1, 2, 3, 2));
        ColorARGB[] pixels = CreateFrame(6, 5, blockSize: 1);
        RefImage<ColorARGB> frame = RefImage<ColorARGB>.Wrap(pixels, 6, 5);

        ColorARGB P(int x, int y) => pixels[(y * 6) + x];
        CollectionAssert.AreEqual(new[] { P(1, 2), P(2, 2), P(3, 2),
                                          P(1, 3), P(2, 3), P(3, 3) },
                                  Write(frame, zone));
    }

    /// <summary>
    /// Level n averages blocks of 2^n x 2^n pixels, starting at the region's position.
    /// </summary>
    [TestMethod]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    public void DownscalesByTheLevel(int downscaleLevel)
    {
        int blockSize = 1 << downscaleLevel;

        using TestScreenCapture screenCapture = new();
        // Starts one block in and down, 3 x 2 blocks large.
        CaptureZone<ColorARGB> zone = screenCapture.RegisterCaptureZone(new CaptureRegion(blockSize, blockSize, blockSize * 3, blockSize * 2), downscaleLevel);
        RefImage<ColorARGB> frame = RefImage<ColorARGB>.Wrap(CreateFrame(blockSize * 5, blockSize * 4, blockSize), blockSize * 5, blockSize * 4);

        Assert.AreEqual(3, zone.Width);
        Assert.AreEqual(2, zone.Height);
        CollectionAssert.AreEqual(new[] { BlockColor(1, 1), BlockColor(2, 1), BlockColor(3, 1),
                                          BlockColor(1, 2), BlockColor(2, 2), BlockColor(3, 2) },
                                  Write(frame, zone));
    }

    /// <summary>
    /// Every zone the capture creates (including the reduced levels of small regions) can be written: its size is the one the downscaling gives.
    /// </summary>
    [TestMethod]
    public void EveryZoneSizeCanBeWritten()
    {
        using TestScreenCapture screenCapture = new();
        RefImage<ColorARGB> frame = RefImage<ColorARGB>.Wrap(CreateFrame(40, 40, blockSize: 1), 40, 40);

        for (int width = 1; width <= 40; width++)
            for (int height = 1; height <= 40; height += 3)
                for (int level = 0; level <= 6; level++)
                {
                    CaptureZone<ColorARGB> zone = screenCapture.RegisterCaptureZone(new CaptureRegion(0, 0, width, height), level);
                    Write(frame, zone);
                    screenCapture.UnregisterCaptureZone(zone);
                }
    }

    [TestMethod]
    public void RegionOutsideTheFrameThrows()
    {
        using TestScreenCapture screenCapture = new();
        CaptureZone<ColorARGB> zone = screenCapture.RegisterCaptureZone(new CaptureRegion(4, 0, 4, 4));
        RefImage<ColorARGB> frame = RefImage<ColorARGB>.Wrap(CreateFrame(6, 6, blockSize: 1), 6, 6);

        try
        {
            Write(frame, zone);
            Assert.Fail("No exception.");
        }
        catch (ArgumentOutOfRangeException) { }
    }

    [TestMethod]
    public void TooSmallBufferThrows()
    {
        using TestScreenCapture screenCapture = new();
        CaptureZone<ColorARGB> zone = screenCapture.RegisterCaptureZone(new CaptureRegion(0, 0, 4, 4));
        RefImage<ColorARGB> frame = RefImage<ColorARGB>.Wrap(CreateFrame(6, 6, blockSize: 1), 6, 6);

        ColorARGB[] buffer = new ColorARGB[(zone.Width * zone.Height) - 1];
        try
        {
            CaptureZoneWriter.Write(frame, zone, buffer);
            Assert.Fail("No exception.");
        }
        catch (ArgumentException) { }
    }

    [TestMethod]
    public void NullZoneThrows()
    {
        RefImage<ColorARGB> frame = RefImage<ColorARGB>.Wrap(CreateFrame(6, 6, blockSize: 1), 6, 6);
        ColorARGB[] buffer = new ColorARGB[16];

        try
        {
            CaptureZoneWriter.Write(frame, null!, buffer);
            Assert.Fail("No exception.");
        }
        catch (ArgumentNullException) { }
    }

    #endregion
}
