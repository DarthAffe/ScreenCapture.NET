using HPPH;

namespace ScreenCapture.NET.Tests;

[TestClass]
public sealed class CaptureZoneSizeTests
{
    [TestMethod]
    [DataRow(100, 100, 0, 100, 100, 0)]
    [DataRow(100, 100, 1, 50, 50, 1)]
    [DataRow(100, 100, 2, 25, 25, 2)]
    [DataRow(101, 51, 1, 50, 25, 1)]
    [DataRow(1920, 1080, 3, 240, 135, 3)]
    [DataRow(4, 4, 5, 1, 1, 2)] // stops scaling once both sides reached 1
    [DataRow(8, 2, 3, 1, 1, 3)] // the short side is clamped to 1 while the long side keeps scaling
    [DataRow(1, 1, 4, 1, 1, 0)]
    public void RegisterCalculatesScaledSize(int width, int height, int downscaleLevel, int expectedWidth, int expectedHeight, int expectedDownscaleLevel)
    {
        using TestScreenCapture screenCapture = new();

        CaptureZone<ColorARGB> zone = screenCapture.RegisterCaptureZone(new CaptureRegion(0, 0, width, height), downscaleLevel);

        Assert.AreEqual(expectedWidth, zone.Width);
        Assert.AreEqual(expectedHeight, zone.Height);
        Assert.AreEqual(expectedDownscaleLevel, zone.DownscaleLevel);
        Assert.AreEqual(new CaptureRegion(0, 0, width, height), zone.Region);
    }

    [TestMethod]
    public void BufferMatchesZoneSize()
    {
        using TestScreenCapture screenCapture = new();

        CaptureZone<ColorARGB> zone = screenCapture.RegisterCaptureZone(new CaptureRegion(10, 20, 101, 51), 1);

        Assert.AreEqual(zone.Width * ColorARGB.ColorFormat.BytesPerPixel, zone.Stride);
        using CaptureFrame<ColorARGB> frame = zone.AcquireFrame();
        Assert.AreEqual((zone.Width, zone.Height, zone.Stride), (frame.Width, frame.Height, frame.Stride));
        Assert.AreEqual(zone.Stride * zone.Height, frame.RawBuffer.Length);
    }

    [TestMethod]
    public void FullScreenZoneIsValid()
    {
        using TestScreenCapture screenCapture = new();

        CaptureZone<ColorARGB> zone = screenCapture.RegisterCaptureZone(new CaptureRegion(0, 0, screenCapture.Display.Width, screenCapture.Display.Height));

        Assert.AreEqual(screenCapture.Display.Width, zone.Width);
        Assert.AreEqual(screenCapture.Display.Height, zone.Height);
    }

    [TestMethod]
    public void ZoneAtBottomRightCornerIsValid()
    {
        using TestScreenCapture screenCapture = new();

        CaptureZone<ColorARGB> zone = screenCapture.RegisterCaptureZone(new CaptureRegion(screenCapture.Display.Width - 1, screenCapture.Display.Height - 1, 1, 1));

        Assert.AreEqual(new CaptureRegion(screenCapture.Display.Width - 1, screenCapture.Display.Height - 1, 1, 1), zone.Region);
    }

    [TestMethod]
    [DataRow(-1, 0, 10, 10)]
    [DataRow(0, -1, 10, 10)]
    [DataRow(0, 0, 0, 10)]
    [DataRow(0, 0, 10, 0)]
    [DataRow(0, 0, -10, 10)]
    [DataRow(0, 0, 10, -10)]
    [DataRow(1911, 0, 10, 10)] // x + width > display width
    [DataRow(0, 1071, 10, 10)] // y + height > display height
    [DataRow(0, 0, 1921, 10)]
    [DataRow(0, 0, 10, 1081)]
    [DataRow(1920, 0, 1, 1)] // starts right of the display
    [DataRow(0, 1080, 1, 1)] // starts below the display
    [DataRow(10, 0, int.MaxValue, 10)] // x + width overflows
    [DataRow(0, 10, 10, int.MaxValue)] // y + height overflows
    [DataRow(int.MaxValue, 0, 10, 10)] // x + width overflows
    [DataRow(0, int.MaxValue, 10, 10)] // y + height overflows
    public void RegisterRejectsInvalidRegion(int x, int y, int width, int height)
    {
        using TestScreenCapture screenCapture = new();

        ArgumentOutOfRangeException exception = Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => screenCapture.RegisterCaptureZone(new CaptureRegion(x, y, width, height)));

        Assert.AreEqual("region", exception.ParamName);
        Assert.AreEqual(new CaptureRegion(x, y, width, height), exception.ActualValue);
    }

    [TestMethod]
    public void RegisterRejectsNegativeDownscaleLevel()
    {
        using TestScreenCapture screenCapture = new();

        ArgumentOutOfRangeException exception = Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => screenCapture.RegisterCaptureZone(new CaptureRegion(0, 0, 16, 8), -1));

        Assert.AreEqual("downscaleLevel", exception.ParamName);
    }
}
