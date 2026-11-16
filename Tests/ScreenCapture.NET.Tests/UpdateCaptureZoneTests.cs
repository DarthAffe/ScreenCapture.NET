using HPPH;

namespace ScreenCapture.NET.Tests;

[TestClass]
public sealed class UpdateCaptureZoneTests
{
    private static readonly CaptureRegion REGION = new(10, 20, 200, 100);
    private const int DOWNSCALE_LEVEL = 1;

    private static (TestScreenCapture screenCapture, CaptureZone<ColorARGB> zone) Create()
    {
        TestScreenCapture screenCapture = new();
        return (screenCapture, screenCapture.RegisterCaptureZone(REGION, DOWNSCALE_LEVEL));
    }

    private static void AssertZone(TestScreenCapture screenCapture, CaptureZone<ColorARGB> zone, CaptureRegion region, int width, int height, int downscaleLevel)
    {
        Assert.AreEqual(region, zone.Region, "Region");
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
    public void RegisterKeepsRegion()
    {
        (TestScreenCapture screenCapture, CaptureZone<ColorARGB> zone) = Create();
        using (screenCapture)
            AssertZone(screenCapture, zone, REGION, 100, 50, DOWNSCALE_LEVEL);
    }

    [TestMethod]
    public void NoParametersChangeNothing()
    {
        (TestScreenCapture screenCapture, CaptureZone<ColorARGB> zone) = Create();
        using (screenCapture)
        {
            screenCapture.UpdateCaptureZone(zone);

            AssertZone(screenCapture, zone, REGION, 100, 50, DOWNSCALE_LEVEL);
        }
    }

    [TestMethod]
    public void SameRegionChangesNothing()
    {
        (TestScreenCapture screenCapture, CaptureZone<ColorARGB> zone) = Create();
        using (screenCapture)
        {
            screenCapture.UpdateCaptureZone(zone, zone.Region);

            AssertZone(screenCapture, zone, REGION, 100, 50, DOWNSCALE_LEVEL);
        }
    }

    [TestMethod]
    public void XOnlyMovesZone()
    {
        (TestScreenCapture screenCapture, CaptureZone<ColorARGB> zone) = Create();
        using (screenCapture)
        {
            screenCapture.UpdateCaptureZone(zone, zone.Region with { X = 300 });

            AssertZone(screenCapture, zone, REGION with { X = 300 }, 100, 50, DOWNSCALE_LEVEL);
        }
    }

    [TestMethod]
    public void YOnlyMovesZone()
    {
        (TestScreenCapture screenCapture, CaptureZone<ColorARGB> zone) = Create();
        using (screenCapture)
        {
            screenCapture.UpdateCaptureZone(zone, zone.Region with { Y = 300 });

            AssertZone(screenCapture, zone, REGION with { Y = 300 }, 100, 50, DOWNSCALE_LEVEL);
        }
    }

    [TestMethod]
    public void WidthOnlyResizesWithCurrentDownscaleLevel()
    {
        (TestScreenCapture screenCapture, CaptureZone<ColorARGB> zone) = Create();
        using (screenCapture)
        {
            screenCapture.UpdateCaptureZone(zone, zone.Region with { Width = 400 });

            AssertZone(screenCapture, zone, REGION with { Width = 400 }, 200, 50, DOWNSCALE_LEVEL);
        }
    }

    [TestMethod]
    public void HeightOnlyResizesWithCurrentDownscaleLevel()
    {
        (TestScreenCapture screenCapture, CaptureZone<ColorARGB> zone) = Create();
        using (screenCapture)
        {
            screenCapture.UpdateCaptureZone(zone, zone.Region with { Height = 400 });

            AssertZone(screenCapture, zone, REGION with { Height = 400 }, 100, 200, DOWNSCALE_LEVEL);
        }
    }

    [TestMethod]
    public void DownscaleLevelOnlyRescalesRegion()
    {
        (TestScreenCapture screenCapture, CaptureZone<ColorARGB> zone) = Create();
        using (screenCapture)
        {
            screenCapture.UpdateCaptureZone(zone, downscaleLevel: 2);
            AssertZone(screenCapture, zone, REGION, 50, 25, 2);

            screenCapture.UpdateCaptureZone(zone, downscaleLevel: 0);
            AssertZone(screenCapture, zone, REGION, REGION.Width, REGION.Height, 0);
        }
    }

    [TestMethod]
    public void AllParametersAreApplied()
    {
        (TestScreenCapture screenCapture, CaptureZone<ColorARGB> zone) = Create();
        using (screenCapture)
        {
            screenCapture.UpdateCaptureZone(zone, new CaptureRegion(1, 2, 64, 32), 3);

            AssertZone(screenCapture, zone, new CaptureRegion(1, 2, 64, 32), 8, 4, 3);
        }
    }

    [TestMethod]
    public void UpdateThroughInterfaceIsApplied()
    {
        (TestScreenCapture screenCapture, CaptureZone<ColorARGB> zone) = Create();
        using (screenCapture)
        {
            ((IScreenCapture)screenCapture).UpdateCaptureZone(zone, new CaptureRegion(1, 2, 64, 32), 3);

            AssertZone(screenCapture, zone, new CaptureRegion(1, 2, 64, 32), 8, 4, 3);
        }
    }

    [TestMethod]
    [DataRow(-1, 20, 200, 100)]
    [DataRow(10, -1, 200, 100)]
    [DataRow(10, 20, 0, 100)]
    [DataRow(10, 20, 200, 0)]
    [DataRow(1800, 20, 200, 100)] // x + width > display width
    [DataRow(10, 1000, 200, 100)] // y + height > display height
    [DataRow(10, 20, 1911, 100)] // x + new width > display width
    [DataRow(10, 20, 200, 1061)] // y + new height > display height
    [DataRow(10, 20, int.MaxValue, 100)] // x + width overflows
    [DataRow(10, 20, 200, int.MaxValue)] // y + height overflows
    public void InvalidUpdateThrowsAndKeepsZone(int x, int y, int width, int height)
    {
        (TestScreenCapture screenCapture, CaptureZone<ColorARGB> zone) = Create();
        using (screenCapture)
        {
            ArgumentOutOfRangeException exception = Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => screenCapture.UpdateCaptureZone(zone, new CaptureRegion(x, y, width, height)));
            Assert.AreEqual("region", exception.ParamName);

            AssertZone(screenCapture, zone, REGION, 100, 50, DOWNSCALE_LEVEL);
        }
    }

    [TestMethod]
    public void NegativeDownscaleLevelThrowsAndKeepsZone()
    {
        (TestScreenCapture screenCapture, CaptureZone<ColorARGB> zone) = Create();
        using (screenCapture)
        {
            ArgumentOutOfRangeException exception = Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => screenCapture.UpdateCaptureZone(zone, downscaleLevel: -1));
            Assert.AreEqual("downscaleLevel", exception.ParamName);

            AssertZone(screenCapture, zone, REGION, 100, 50, DOWNSCALE_LEVEL);
        }
    }

    [TestMethod]
    public void UpdatingUnregisteredZoneThrows()
    {
        (TestScreenCapture screenCapture, CaptureZone<ColorARGB> zone) = Create();
        using (screenCapture)
        {
            screenCapture.UnregisterCaptureZone(zone);

            Assert.ThrowsExactly<ArgumentException>(() => screenCapture.UpdateCaptureZone(zone, zone.Region with { X = 0 }));
        }
    }

    [TestMethod]
    public void UpdatingZoneOfOtherCaptureThrows()
    {
        (TestScreenCapture screenCapture, CaptureZone<ColorARGB> zone) = Create();
        using (screenCapture)
        using (TestScreenCapture otherScreenCapture = new())
            Assert.ThrowsExactly<ArgumentException>(() => otherScreenCapture.UpdateCaptureZone(zone, zone.Region with { X = 0 }));
    }

    [TestMethod]
    public void UpdatingNullZoneThrows()
    {
        using TestScreenCapture screenCapture = new();

        Assert.ThrowsExactly<ArgumentNullException>(() => screenCapture.UpdateCaptureZone(null!, new CaptureRegion(0, 0, 1, 1)));
    }
}
