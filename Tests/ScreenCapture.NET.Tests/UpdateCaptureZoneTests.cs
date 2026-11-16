using HPPH;

namespace ScreenCapture.NET.Tests;

[TestClass]
public sealed class UpdateCaptureZoneTests
{
    private const int X = 10;
    private const int Y = 20;
    private const int WIDTH = 200;
    private const int HEIGHT = 100;
    private const int DOWNSCALE_LEVEL = 1;

    private static (TestScreenCapture screenCapture, CaptureZone<ColorARGB> zone) Create()
    {
        TestScreenCapture screenCapture = new();
        return (screenCapture, screenCapture.RegisterCaptureZone(X, Y, WIDTH, HEIGHT, DOWNSCALE_LEVEL));
    }

    private static void AssertZone(TestScreenCapture screenCapture, CaptureZone<ColorARGB> zone, int x, int y, int unscaledWidth, int unscaledHeight, int width, int height, int downscaleLevel)
    {
        Assert.AreEqual(x, zone.X, "X");
        Assert.AreEqual(y, zone.Y, "Y");
        Assert.AreEqual(unscaledWidth, zone.UnscaledWidth, "UnscaledWidth");
        Assert.AreEqual(unscaledHeight, zone.UnscaledHeight, "UnscaledHeight");
        Assert.AreEqual(width, zone.Width, "Width");
        Assert.AreEqual(height, zone.Height, "Height");
        Assert.AreEqual(downscaleLevel, zone.DownscaleLevel, "DownscaleLevel");

        // The buffers get the new size with the next capture.
        screenCapture.CaptureScreen();
        using CaptureFrame<ColorARGB> frame = zone.Lock();
        Assert.AreEqual((zone.Width, zone.Height, zone.Stride), (frame.Width, frame.Height, frame.Stride), "Frame size");
        Assert.AreEqual(zone.Stride * zone.Height, frame.RawBuffer.Length, "Buffer size");
    }

    [TestMethod]
    public void NoParametersChangeNothing()
    {
        (TestScreenCapture screenCapture, CaptureZone<ColorARGB> zone) = Create();
        using (screenCapture)
        {
            screenCapture.UpdateCaptureZone(zone);

            AssertZone(screenCapture, zone, X, Y, WIDTH, HEIGHT, 100, 50, DOWNSCALE_LEVEL);
        }
    }

    [TestMethod]
    public void XOnlyMovesZone()
    {
        (TestScreenCapture screenCapture, CaptureZone<ColorARGB> zone) = Create();
        using (screenCapture)
        {
            screenCapture.UpdateCaptureZone(zone, x: 300);

            AssertZone(screenCapture, zone, 300, Y, WIDTH, HEIGHT, 100, 50, DOWNSCALE_LEVEL);
        }
    }

    [TestMethod]
    public void YOnlyMovesZone()
    {
        (TestScreenCapture screenCapture, CaptureZone<ColorARGB> zone) = Create();
        using (screenCapture)
        {
            screenCapture.UpdateCaptureZone(zone, y: 300);

            AssertZone(screenCapture, zone, X, 300, WIDTH, HEIGHT, 100, 50, DOWNSCALE_LEVEL);
        }
    }

    [TestMethod]
    public void WidthOnlyResizesWithCurrentDownscaleLevel()
    {
        (TestScreenCapture screenCapture, CaptureZone<ColorARGB> zone) = Create();
        using (screenCapture)
        {
            screenCapture.UpdateCaptureZone(zone, width: 400);

            AssertZone(screenCapture, zone, X, Y, 400, HEIGHT, 200, 50, DOWNSCALE_LEVEL);
        }
    }

    [TestMethod]
    public void HeightOnlyResizesWithCurrentDownscaleLevel()
    {
        (TestScreenCapture screenCapture, CaptureZone<ColorARGB> zone) = Create();
        using (screenCapture)
        {
            screenCapture.UpdateCaptureZone(zone, height: 400);

            AssertZone(screenCapture, zone, X, Y, WIDTH, 400, 100, 200, DOWNSCALE_LEVEL);
        }
    }

    [TestMethod]
    public void DownscaleLevelOnlyRescalesUnscaledSize()
    {
        (TestScreenCapture screenCapture, CaptureZone<ColorARGB> zone) = Create();
        using (screenCapture)
        {
            screenCapture.UpdateCaptureZone(zone, downscaleLevel: 2);
            AssertZone(screenCapture, zone, X, Y, WIDTH, HEIGHT, 50, 25, 2);

            screenCapture.UpdateCaptureZone(zone, downscaleLevel: 0);
            AssertZone(screenCapture, zone, X, Y, WIDTH, HEIGHT, WIDTH, HEIGHT, 0);
        }
    }

    [TestMethod]
    public void AllParametersAreApplied()
    {
        (TestScreenCapture screenCapture, CaptureZone<ColorARGB> zone) = Create();
        using (screenCapture)
        {
            screenCapture.UpdateCaptureZone(zone, 1, 2, 64, 32, 3);

            AssertZone(screenCapture, zone, 1, 2, 64, 32, 8, 4, 3);
        }
    }

    [TestMethod]
    [DataRow(-1, null, null, null)]
    [DataRow(null, -1, null, null)]
    [DataRow(null, null, 0, null)]
    [DataRow(null, null, null, 0)]
    [DataRow(1800, null, null, null)] // new x + current width > display width
    [DataRow(null, 1000, null, null)] // new y + current height > display height
    [DataRow(null, null, 1911, null)] // current x + new width > display width
    [DataRow(null, null, null, 1061)] // current y + new height > display height
    public void InvalidUpdateThrowsAndKeepsZone(int? x, int? y, int? width, int? height)
    {
        (TestScreenCapture screenCapture, CaptureZone<ColorARGB> zone) = Create();
        using (screenCapture)
        {
            Assert.ThrowsExactly<ArgumentException>(() => screenCapture.UpdateCaptureZone(zone, x, y, width, height));

            AssertZone(screenCapture, zone, X, Y, WIDTH, HEIGHT, 100, 50, DOWNSCALE_LEVEL);
        }
    }

    [TestMethod]
    public void UpdatingUnregisteredZoneThrows()
    {
        (TestScreenCapture screenCapture, CaptureZone<ColorARGB> zone) = Create();
        using (screenCapture)
        {
            screenCapture.UnregisterCaptureZone(zone);

            Assert.ThrowsExactly<ArgumentException>(() => screenCapture.UpdateCaptureZone(zone, x: 0));
        }
    }

    [TestMethod]
    public void UpdatingZoneOfOtherCaptureThrows()
    {
        (TestScreenCapture screenCapture, CaptureZone<ColorARGB> zone) = Create();
        using (screenCapture)
        using (TestScreenCapture otherScreenCapture = new())
            Assert.ThrowsExactly<ArgumentException>(() => otherScreenCapture.UpdateCaptureZone(zone, x: 0));
    }

    [TestMethod]
    public void UpdatingNullZoneThrows()
    {
        using TestScreenCapture screenCapture = new();

        Assert.ThrowsExactly<ArgumentNullException>(() => screenCapture.UpdateCaptureZone(null!, x: 0));
    }
}
