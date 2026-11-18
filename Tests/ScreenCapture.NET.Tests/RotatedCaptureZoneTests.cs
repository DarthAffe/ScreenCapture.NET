using System.Runtime.InteropServices;
using HPPH;

namespace ScreenCapture.NET.Tests;

/// <summary>
/// The zone writes of <see cref="AbstractScreenCapture{TColor}"/> for sources in the native orientation of the output: which pixels end up in the zone, in which order.
/// The reference is the same zone written from the frame in the orientation of the display (see <see cref="WriteCaptureZoneTests"/>).
/// </summary>
[TestClass]
public sealed class RotatedCaptureZoneTests
{
    #region Constants

    // Not divisible by any block size, so every level drops columns and rows.
    private const int DISPLAY_WIDTH = 11;
    private const int DISPLAY_HEIGHT = 7;

    #endregion

    #region Methods

    private static ImageRotation Inverse(ImageRotation rotation)
        => rotation switch
        {
            ImageRotation.Rotate90 => ImageRotation.Rotate270,
            ImageRotation.Rotate270 => ImageRotation.Rotate90,
            _ => rotation
        };

    // Rotates the pixels of an image of width x height pixels clockwise; the result has the rotated size.
    private static ColorARGB[] Rotate(ColorARGB[] pixels, int width, int height, ImageRotation rotation)
    {
        ColorARGB[] rotated = new ColorARGB[pixels.Length];
        RefImage<ColorARGB>.Wrap(pixels, width, height).Rotate(rotation, rotated);
        return rotated;
    }

    // Random pixels: the averages of the blocks differ, whichever pixels end up in them.
    private static ColorARGB[] CreateDisplayFrame()
    {
        Random random = new(42);
        ColorARGB[] pixels = new ColorARGB[DISPLAY_WIDTH * DISPLAY_HEIGHT];
        for (int i = 0; i < pixels.Length; i++)
            pixels[i] = new ColorARGB((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256));

        return pixels;
    }

    private static ColorARGB[] Write(CaptureZone<ColorARGB> zone, Action<byte[]> write)
    {
        byte[] buffer = new byte[zone.Stride * zone.Height];
        write(buffer);
        return MemoryMarshal.Cast<byte, ColorARGB>(buffer).ToArray();
    }

    // Copies a region out of an image, so it's an image of its own (with row padding, like a mapped texture).
    private static (byte[] data, int stride) Crop(ColorARGB[] pixels, int width, CaptureRegion region)
    {
        int stride = (region.Width + 3) * 4;
        byte[] data = new byte[stride * region.Height];
        for (int y = 0; y < region.Height; y++)
            MemoryMarshal.AsBytes(pixels.AsSpan(((region.Y + y) * width) + region.X, region.Width)).CopyTo(data.AsSpan(y * stride));

        return (data, stride);
    }

    [TestMethod]
    [DataRow(ImageRotation.None, 10, 20, 300, 400)]
    [DataRow(ImageRotation.Rotate90, 20, 1920 - 10 - 300, 400, 300)]
    [DataRow(ImageRotation.Rotate180, 1920 - 10 - 300, 1080 - 20 - 400, 300, 400)]
    [DataRow(ImageRotation.Rotate270, 1080 - 20 - 400, 10, 400, 300)]
    public void SourceRegion(ImageRotation rotation, int expectedX, int expectedY, int expectedWidth, int expectedHeight)
        => Assert.AreEqual(new CaptureRegion(expectedX, expectedY, expectedWidth, expectedHeight),
                           TestScreenCapture.SourceRegion(new CaptureRegion(10, 20, 300, 400), TestScreenCapture.CreateDisplay(1920, 1080, rotation)));

    /// <summary>
    /// Every region at every level shows the same pixels, whether the frame, the cropped region or the downscaled region is in the native orientation and rotated by the helper
    /// or the frame is in the orientation of the display.
    /// </summary>
    [TestMethod]
    [DataRow(ImageRotation.None)]
    [DataRow(ImageRotation.Rotate90)]
    [DataRow(ImageRotation.Rotate180)]
    [DataRow(ImageRotation.Rotate270)]
    public void NativeOrientationMatchesTheDisplay(ImageRotation rotation)
    {
        using TestScreenCapture screenCapture = new();
        Display display = TestScreenCapture.CreateDisplay(DISPLAY_WIDTH, DISPLAY_HEIGHT, rotation);

        ColorARGB[] displayFrame = CreateDisplayFrame();
        ColorARGB[] nativeFrame = Rotate(displayFrame, DISPLAY_WIDTH, DISPLAY_HEIGHT, Inverse(rotation));
        (int nativeWidth, int nativeHeight) = PixelHelper.GetRotatedSize(DISPLAY_WIDTH, DISPLAY_HEIGHT, rotation);

        for (int level = 0; level <= 3; level++)
            for (int y = 0; y < DISPLAY_HEIGHT; y++)
                for (int x = 0; x < DISPLAY_WIDTH; x++)
                    for (int height = 1; (y + height) <= DISPLAY_HEIGHT; height++)
                        for (int width = 1; (x + width) <= DISPLAY_WIDTH; width++)
                        {
                            CaptureZone<ColorARGB> zone = screenCapture.RegisterCaptureZone(new CaptureRegion(x, y, width, height), level);
                            string name = $"Zone ({x}, {y}, {width}, {height}) at level {level}";

                            ColorARGB[] expected = Write(zone, buffer => TestScreenCapture.Write(RefImage<ColorARGB>.Wrap(displayFrame, DISPLAY_WIDTH, DISPLAY_HEIGHT), zone, buffer));

                            ColorARGB[] fromFrame = Write(zone, buffer => TestScreenCapture.Write(RefImage<ColorARGB>.Wrap(nativeFrame, nativeWidth, nativeHeight), zone, buffer, rotation));
                            CollectionAssert.AreEqual(expected, fromFrame, $"{name}, from the frame.");

                            CaptureRegion source = TestScreenCapture.SourceRegion(zone.Region, display);
                            (byte[] region, int regionStride) = Crop(nativeFrame, nativeWidth, source);
                            ColorARGB[] fromRegion = Write(zone, buffer => TestScreenCapture.WriteFromRegion(RefImage<ColorARGB>.Wrap(region, source.Width, source.Height, regionStride), zone, buffer, rotation));
                            CollectionAssert.AreEqual(expected, fromRegion, $"{name}, from the region.");

                            ColorARGB[] scaled = Rotate(expected, zone.Width, zone.Height, Inverse(rotation));
                            (int scaledWidth, int scaledHeight) = PixelHelper.GetRotatedSize(zone.Width, zone.Height, rotation);
                            ColorARGB[] fromScaled = Write(zone, buffer => TestScreenCapture.WriteFromScaled(RefImage<ColorARGB>.Wrap(scaled, scaledWidth, scaledHeight), zone, buffer, rotation));
                            CollectionAssert.AreEqual(expected, fromScaled, $"{name}, from the scaled region.");

                            screenCapture.UnregisterCaptureZone(zone);
                        }
    }

    /// <summary>
    /// The direction of the rotation (pinned, it's confirmed on hardware): source pixel S(row, column) of a source with padded rows, written into a zone of 3x2.
    /// </summary>
    [TestMethod]
    [DataRow(ImageRotation.None, new[] { 0, 0, 0, 1, 0, 2, 1, 0, 1, 1, 1, 2 })]
    [DataRow(ImageRotation.Rotate90, new[] { 2, 0, 1, 0, 0, 0, 2, 1, 1, 1, 0, 1 })]
    [DataRow(ImageRotation.Rotate180, new[] { 1, 2, 1, 1, 1, 0, 0, 2, 0, 1, 0, 0 })]
    [DataRow(ImageRotation.Rotate270, new[] { 0, 1, 1, 1, 2, 1, 0, 0, 1, 0, 2, 0 })]
    public void RotatesClockwise(ImageRotation rotation, int[] expectedRowsAndColumns)
    {
        using TestScreenCapture screenCapture = new();
        CaptureZone<ColorARGB> zone = screenCapture.RegisterCaptureZone(new CaptureRegion(0, 0, 3, 2));

        (int columns, int rows) = PixelHelper.GetRotatedSize(3, 2, rotation);
        ColorARGB[] source = new ColorARGB[columns * rows];
        for (int row = 0; row < rows; row++)
            for (int column = 0; column < columns; column++)
                source[(row * columns) + column] = S(row, column);
        (byte[] padded, int stride) = Crop(source, columns, new CaptureRegion(0, 0, columns, rows));

        ColorARGB[] expected = new ColorARGB[6];
        for (int i = 0; i < expected.Length; i++)
            expected[i] = S(expectedRowsAndColumns[i * 2], expectedRowsAndColumns[(i * 2) + 1]);

        CollectionAssert.AreEqual(expected, Write(zone, buffer => TestScreenCapture.WriteFromScaled(RefImage<ColorARGB>.Wrap(padded, columns, rows, stride), zone, buffer, rotation)));

        static ColorARGB S(int row, int column) => new(255, (byte)column, (byte)row, 0x42);
    }

    /// <summary>
    /// The region of the zone is checked against the frame rotated into the orientation of the display.
    /// </summary>
    [TestMethod]
    public void RegionOutsideTheRotatedFrameThrows()
    {
        using TestScreenCapture screenCapture = new();
        // Within the frame of 6x4 itself, but not within the display of 4x6.
        CaptureZone<ColorARGB> zone = screenCapture.RegisterCaptureZone(new CaptureRegion(0, 0, 5, 4));
        ColorARGB[] frame = new ColorARGB[6 * 4];

        try
        {
            Write(zone, buffer => TestScreenCapture.Write(RefImage<ColorARGB>.Wrap(frame, 6, 4), zone, buffer, ImageRotation.Rotate90));
            Assert.Fail("No exception.");
        }
        catch (ArgumentOutOfRangeException) { }
    }

    [TestMethod]
    public void RegionOfAnotherSizeThrows()
    {
        using TestScreenCapture screenCapture = new();
        CaptureZone<ColorARGB> zone = screenCapture.RegisterCaptureZone(new CaptureRegion(0, 0, 4, 6));
        // 4x6 itself, but 6x4 after the rotation.
        ColorARGB[] region = new ColorARGB[4 * 6];

        try
        {
            Write(zone, buffer => TestScreenCapture.WriteFromRegion(RefImage<ColorARGB>.Wrap(region, 4, 6), zone, buffer, ImageRotation.Rotate90));
            Assert.Fail("No exception.");
        }
        catch (ArgumentException) { }
    }

    [TestMethod]
    public void ScaledOfAnotherSizeThrows()
    {
        using TestScreenCapture screenCapture = new();
        CaptureZone<ColorARGB> zone = screenCapture.RegisterCaptureZone(new CaptureRegion(0, 0, 8, 12), 1);
        // 3x6 after the rotation: it fits into the buffer of the zone (4x6), but would leave a column unwritten.
        ColorARGB[] scaled = new ColorARGB[6 * 3];

        try
        {
            Write(zone, buffer => TestScreenCapture.WriteFromScaled(RefImage<ColorARGB>.Wrap(scaled, 6, 3), zone, buffer, ImageRotation.Rotate270));
            Assert.Fail("No exception.");
        }
        catch (ArgumentException) { }
    }

    #endregion
}
