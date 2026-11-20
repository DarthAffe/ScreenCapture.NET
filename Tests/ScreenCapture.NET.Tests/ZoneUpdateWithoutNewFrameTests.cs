using HPPH;

namespace ScreenCapture.NET.Tests;

/// <summary>
/// Captures without a new frame (NoChange, Timeout) only write zones that requested an update or don't show the last frame yet;
/// captures without a frame (Restarting, Unavailable, Failed, after a restart) write none.
/// </summary>
[TestClass]
public sealed class ZoneUpdateWithoutNewFrameTests
{
    private static readonly CaptureRegion REGION = new(0, 0, 4, 4);

    [TestMethod]
    [DataRow(CaptureResult.NoChange)]
    [DataRow(CaptureResult.Timeout)]
    public void UpToDateZoneIsSkipped(CaptureResult result)
    {
        using TestScreenCapture screenCapture = new();
        WriteCounter zone = new(screenCapture.RegisterCaptureZone(REGION));

        Capture(screenCapture, CaptureResult.NewFrame);
        Assert.AreEqual(1, zone.Writes);

        for (int i = 0; i < 3; i++)
            Capture(screenCapture, result);
        Assert.AreEqual(1, zone.Writes);
    }

    [TestMethod]
    [DataRow(CaptureResult.NoChange)]
    [DataRow(CaptureResult.Timeout)]
    public void RequestedZoneIsWrittenFromTheLastFrame(CaptureResult result)
    {
        using TestScreenCapture screenCapture = new();
        WriteCounter zone = new(screenCapture.RegisterCaptureZone(REGION));

        Capture(screenCapture, CaptureResult.NewFrame);

        zone.Zone.RequestUpdate();
        Capture(screenCapture, result);
        Assert.AreEqual(2, zone.Writes);
        Assert.IsFalse(zone.Zone.IsUpdateRequested);

        Capture(screenCapture, result);
        Assert.AreEqual(2, zone.Writes);
    }

    [TestMethod]
    [DataRow(CaptureResult.Restarting)]
    [DataRow(CaptureResult.Unavailable)]
    [DataRow(CaptureResult.Failed)]
    public void NoZoneIsWrittenWithoutAFrame(CaptureResult result)
    {
        using TestScreenCapture screenCapture = new();
        WriteCounter requested = new(screenCapture.RegisterCaptureZone(REGION));

        Capture(screenCapture, CaptureResult.NewFrame);

        requested.Zone.RequestUpdate();
        WriteCounter added = new(screenCapture.RegisterCaptureZone(REGION));

        Capture(screenCapture, result);

        // The frame stays gone until the next new frame: the backend may have dropped or reset it.
        Capture(screenCapture, CaptureResult.NoChange);
        Capture(screenCapture, CaptureResult.Timeout);

        Assert.AreEqual(1, requested.Writes);
        Assert.AreEqual(0, added.Writes);
        Assert.IsTrue(requested.Zone.IsUpdateRequested);

        Capture(screenCapture, CaptureResult.NewFrame);

        Assert.AreEqual(2, requested.Writes);
        Assert.AreEqual(1, added.Writes);
        Assert.IsFalse(requested.Zone.IsUpdateRequested);
    }

    [TestMethod]
    public void NoZoneIsWrittenBeforeTheFirstFrame()
    {
        using TestScreenCapture screenCapture = new();
        WriteCounter zone = new(screenCapture.RegisterCaptureZone(REGION));
        zone.Zone.RequestUpdate();

        Capture(screenCapture, CaptureResult.Timeout);
        Capture(screenCapture, CaptureResult.NoChange);

        Assert.AreEqual(0, zone.Writes);
        Assert.IsTrue(zone.Zone.IsUpdateRequested);
    }

    [TestMethod]
    public void RestartDropsTheFrame()
    {
        using TestScreenCapture screenCapture = new();
        WriteCounter zone = new(screenCapture.RegisterCaptureZone(REGION));

        Capture(screenCapture, CaptureResult.NewFrame);

        screenCapture.Restart();
        zone.Zone.RequestUpdate();
        Capture(screenCapture, CaptureResult.Timeout);
        Assert.AreEqual(1, zone.Writes);

        Capture(screenCapture, CaptureResult.NewFrame);
        Assert.AreEqual(2, zone.Writes);
    }

    [TestMethod]
    public void NewZoneIsFilledOnAStaticScreen()
    {
        using TestScreenCapture screenCapture = new();
        Capture(screenCapture, CaptureResult.NewFrame);

        WriteCounter zone = new(screenCapture.RegisterCaptureZone(REGION));

        Capture(screenCapture, CaptureResult.Timeout);
        Assert.AreEqual(1, zone.Writes);

        Capture(screenCapture, CaptureResult.Timeout);
        Assert.AreEqual(1, zone.Writes);
    }

    [TestMethod]
    public void ChangedZoneIsWrittenAgainOnAStaticScreen()
    {
        using TestScreenCapture screenCapture = new();
        CaptureZone<ColorARGB> captureZone = screenCapture.RegisterCaptureZone(REGION);
        WriteCounter zone = new(captureZone);

        Capture(screenCapture, CaptureResult.NewFrame);

        screenCapture.UpdateCaptureZone(captureZone, REGION with { X = 10 });
        Capture(screenCapture, CaptureResult.Timeout);
        Assert.AreEqual(2, zone.Writes);

        screenCapture.UpdateCaptureZone(captureZone, downscaleLevel: 1);
        Capture(screenCapture, CaptureResult.NoChange);
        Assert.AreEqual(3, zone.Writes);

        // An update that changes nothing doesn't need the pixels again.
        screenCapture.UpdateCaptureZone(captureZone, captureZone.Region, captureZone.DownscaleLevel);
        Capture(screenCapture, CaptureResult.Timeout);
        Assert.AreEqual(3, zone.Writes);
    }

    [TestMethod]
    public void UnwrittenZoneIsWrittenOnAStaticScreen()
    {
        using TestScreenCapture screenCapture = new() { WriteZones = false };
        WriteCounter zone = new(screenCapture.RegisterCaptureZone(REGION));

        // E.g. the backend had no data for the zone.
        Capture(screenCapture, CaptureResult.NewFrame);
        Assert.AreEqual(0, zone.Writes);

        screenCapture.WriteZones = true;
        Capture(screenCapture, CaptureResult.NoChange);
        Assert.AreEqual(1, zone.Writes);

        Capture(screenCapture, CaptureResult.NoChange);
        Assert.AreEqual(1, zone.Writes);
    }

    /// <summary>
    /// A held frame makes a zone skip a new frame. If the screen then stays static,
    /// the zone must still get that frame, or it shows an old one until the screen changes.
    /// </summary>
    [TestMethod]
    public void ZoneThatSkippedAFrameIsWrittenOnAStaticScreen()
    {
        ColorARGB first = new(255, 1, 0, 0);
        ColorARGB second = new(255, 2, 0, 0);
        ColorARGB third = new(255, 3, 0, 0);

        using TestScreenCapture screenCapture = new();
        CaptureZone<ColorARGB> captureZone = screenCapture.RegisterCaptureZone(REGION);
        WriteCounter zone = new(captureZone);

        screenCapture.Pattern = (_, _) => first;
        Capture(screenCapture, CaptureResult.NewFrame);

        CaptureFrame<ColorARGB> frame = captureZone.Lock();
        try
        {
            screenCapture.Pattern = (_, _) => second;
            Capture(screenCapture, CaptureResult.NewFrame);

            // The back buffer is the one the frame holds: skipped.
            screenCapture.Pattern = (_, _) => third;
            Capture(screenCapture, CaptureResult.NewFrame);
            Assert.AreEqual(2, zone.Writes);
            Assert.AreEqual(first, frame.Pixels[0]);
        }
        finally
        {
            frame.Dispose();
        }

        Capture(screenCapture, CaptureResult.Timeout);
        Assert.AreEqual(3, zone.Writes);

        using CaptureFrame<ColorARGB> current = captureZone.Lock();
        Assert.AreEqual(third, current.Pixels[0]);
    }

    [TestMethod]
    public void DisabledAutoUpdateZoneIsOnlyWrittenOnRequest()
    {
        using TestScreenCapture screenCapture = new();
        WriteCounter zone = new(screenCapture.RegisterCaptureZone(REGION));
        zone.Zone.AutoUpdate = false;

        Capture(screenCapture, CaptureResult.NewFrame);
        Capture(screenCapture, CaptureResult.NoChange);
        Assert.AreEqual(0, zone.Writes);

        zone.Zone.RequestUpdate();
        Capture(screenCapture, CaptureResult.NoChange);
        Assert.AreEqual(1, zone.Writes);

        Capture(screenCapture, CaptureResult.NewFrame);
        Assert.AreEqual(1, zone.Writes);
    }

    [TestMethod]
    public void ReenabledAutoUpdateZoneCatchesUpOnAStaticScreen()
    {
        using TestScreenCapture screenCapture = new();
        WriteCounter zone = new(screenCapture.RegisterCaptureZone(REGION));
        zone.Zone.AutoUpdate = false;

        Capture(screenCapture, CaptureResult.NewFrame);

        zone.Zone.AutoUpdate = true;
        Capture(screenCapture, CaptureResult.Timeout);
        Assert.AreEqual(1, zone.Writes);
    }

    private static void Capture(TestScreenCapture screenCapture, CaptureResult result)
    {
        screenCapture.Result = result;
        Assert.AreEqual(result, screenCapture.CaptureScreen());
    }

    private sealed class WriteCounter
    {
        public CaptureZone<ColorARGB> Zone { get; }

        public int Writes { get; private set; }

        public WriteCounter(CaptureZone<ColorARGB> zone)
        {
            Zone = zone;
            zone.Updated += (_, _) => Writes++;
        }
    }
}
