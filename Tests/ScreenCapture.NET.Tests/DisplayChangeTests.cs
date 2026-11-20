using HPPH;

namespace ScreenCapture.NET.Tests;

/// <summary>
/// A restart that finds the display changed updates the display of the capture and its zones, makes zones that don't fit anymore invalid
/// and raises DisplayChanged with the next capture, before the zones are written.
/// </summary>
[TestClass]
public sealed class DisplayChangeTests
{
    private static readonly Display ORIGINAL = TestScreenCapture.CreateDisplay(1920, 1080);
    private static readonly Display SMALLER = TestScreenCapture.CreateDisplay(1280, 720);
    private static readonly Display ROTATED = TestScreenCapture.CreateDisplay(1080, 1920, ImageRotation.Rotate90);

    private static readonly ColorARGB FIRST = new(255, 1, 2, 3);
    private static readonly ColorARGB SECOND = new(255, 4, 5, 6);

    [TestMethod]
    public void RestartUpdatesTheDisplayOfTheCaptureAndItsZones()
    {
        using TestScreenCapture screenCapture = new();
        CaptureZone<ColorARGB> zone = screenCapture.RegisterCaptureZone(new CaptureRegion(0, 0, 16, 16));

        ChangeDisplay(screenCapture, SMALLER);

        Assert.AreEqual(SMALLER, screenCapture.Display);
        Assert.AreEqual(SMALLER, zone.Display);
        Assert.IsTrue(zone.IsValid);

        // New zones are validated against the new display.
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => screenCapture.RegisterCaptureZone(new CaptureRegion(1280, 0, 16, 16)));
        Assert.AreEqual(SMALLER, screenCapture.RegisterCaptureZone(new CaptureRegion(1264, 704, 16, 16)).Display);
    }

    [TestMethod]
    public void ChangeIsRaisedByTheNextCapture()
    {
        using TestScreenCapture screenCapture = new();
        List<ScreenCaptureDisplayChangedEventArgs> changes = [];
        screenCapture.DisplayChanged += (_, e) => changes.Add(e);

        ChangeDisplay(screenCapture, SMALLER);
        Assert.IsEmpty(changes);

        screenCapture.CaptureScreen();
        Assert.HasCount(1, changes);
        Assert.AreEqual(new ScreenCaptureDisplayChangedEventArgs(ORIGINAL, SMALLER), changes[0]);

        // A restart that finds the same display changes nothing.
        screenCapture.CaptureScreen();
        screenCapture.Restart();
        screenCapture.CaptureScreen();
        Assert.HasCount(1, changes);
    }

    [TestMethod]
    public void ChangeFoundByACaptureIsRaisedByIt()
    {
        using TestScreenCapture screenCapture = new();
        CaptureZone<ColorARGB> zone = screenCapture.RegisterCaptureZone(new CaptureRegion(1800, 0, 16, 16));

        List<ScreenCaptureDisplayChangedEventArgs> changes = [];
        bool? zoneValidInHandler = null;
        screenCapture.DisplayChanged += (_, e) =>
        {
            changes.Add(e);
            zoneValidInHandler = zone.IsValid;
        };

        // The backend lost its source (e.g. after a mode change): the capture restarts it, which finds the new display.
        screenCapture.IsRunning = false;
        screenCapture.NextDisplay = SMALLER;
        Assert.AreEqual(CaptureResult.Restarting, screenCapture.CaptureScreen());

        Assert.HasCount(1, changes);
        Assert.AreEqual(SMALLER, changes[0].Display);
        Assert.IsFalse(zoneValidInHandler, "The zones didn't follow the display before the event.");
    }

    [TestMethod]
    public void HandlerCanFitZonesBeforeTheyAreWritten()
    {
        using TestScreenCapture screenCapture = new();
        CaptureZone<ColorARGB> zone = screenCapture.RegisterCaptureZone(new CaptureRegion(1800, 0, 100, 100));

        List<string> order = [];
        zone.Updated += (_, _) => order.Add("zone");
        screenCapture.DisplayChanged += (_, e) =>
        {
            order.Add("display");
            screenCapture.UpdateCaptureZone(zone, zone.Region with { X = e.Display.Width - zone.Region.Width });
        };

        ChangeDisplay(screenCapture, SMALLER);
        Assert.IsFalse(zone.IsValid);

        Assert.AreEqual(CaptureResult.NewFrame, screenCapture.CaptureScreen());

        CollectionAssert.AreEqual(new[] { "display", "zone" }, order);
        Assert.IsTrue(zone.IsValid);
        Assert.AreEqual(new CaptureRegion(1180, 0, 100, 100), zone.Region);
    }

    [TestMethod]
    public void ZonesOutsideTheNewDisplayBecomeInvalid()
    {
        using TestScreenCapture screenCapture = new();
        WriteCounter inside = new(screenCapture.RegisterCaptureZone(new CaptureRegion(0, 0, 100, 100)));
        WriteCounter edge = new(screenCapture.RegisterCaptureZone(new CaptureRegion(1180, 620, 100, 100)));
        WriteCounter partlyOutside = new(screenCapture.RegisterCaptureZone(new CaptureRegion(1200, 0, 100, 100)));
        WriteCounter below = new(screenCapture.RegisterCaptureZone(new CaptureRegion(0, 1000, 100, 80)));
        WriteCounter outside = new(screenCapture.RegisterCaptureZone(new CaptureRegion(1500, 800, 100, 100)));
        WriteCounter[] valid = [inside, edge];
        WriteCounter[] invalid = [partlyOutside, below, outside];

        screenCapture.Pattern = (_, _) => FIRST;
        screenCapture.CaptureScreen();

        ChangeDisplay(screenCapture, SMALLER);

        foreach (WriteCounter zone in valid)
            Assert.IsTrue(zone.Zone.IsValid, $"{zone.Zone.Region}");

        foreach (WriteCounter zone in invalid)
        {
            Assert.IsFalse(zone.Zone.IsValid, $"{zone.Zone.Region}");
            Assert.AreEqual(SMALLER, zone.Zone.Display);
        }

        // Invalid zones keep their region, their pixels and their update request.
        partlyOutside.Zone.RequestUpdate();
        screenCapture.Pattern = (_, _) => SECOND;
        screenCapture.CaptureScreen();

        foreach (WriteCounter zone in valid)
        {
            Assert.AreEqual(2, zone.Writes, $"{zone.Zone.Region}");
            Assert.AreEqual(SECOND, GetFirstPixel(zone.Zone), $"{zone.Zone.Region}");
        }

        foreach (WriteCounter zone in invalid)
        {
            Assert.AreEqual(1, zone.Writes, $"{zone.Zone.Region}");
            Assert.AreEqual(FIRST, GetFirstPixel(zone.Zone), $"{zone.Zone.Region}");
        }

        Assert.AreEqual(new CaptureRegion(1200, 0, 100, 100), partlyOutside.Zone.Region);
        Assert.IsTrue(partlyOutside.Zone.IsUpdateRequested);
    }

    [TestMethod]
    public void RotationSwapsTheBounds()
    {
        using TestScreenCapture screenCapture = new();
        CaptureZone<ColorARGB> wide = screenCapture.RegisterCaptureZone(new CaptureRegion(0, 0, 1920, 100));
        CaptureZone<ColorARGB> corner = screenCapture.RegisterCaptureZone(new CaptureRegion(0, 0, 100, 100));

        ChangeDisplay(screenCapture, ROTATED);

        Assert.IsFalse(wide.IsValid);
        Assert.IsTrue(corner.IsValid);

        // Regions that didn't fit before fit now.
        screenCapture.UpdateCaptureZone(wide, new CaptureRegion(0, 0, 1080, 1920));
        Assert.IsTrue(wide.IsValid);
    }

    [TestMethod]
    public void UpdateWithAFittingRegionMakesAZoneValid()
    {
        using TestScreenCapture screenCapture = new();
        WriteCounter zone = new(screenCapture.RegisterCaptureZone(new CaptureRegion(1800, 0, 100, 100), downscaleLevel: 1));
        screenCapture.CaptureScreen();

        ChangeDisplay(screenCapture, SMALLER);

        // The region is checked against the new display, also if it's not changed.
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => screenCapture.UpdateCaptureZone(zone.Zone, downscaleLevel: 2));
        Assert.IsFalse(zone.Zone.IsValid);
        Assert.AreEqual(1, zone.Zone.DownscaleLevel);

        screenCapture.UpdateCaptureZone(zone.Zone, new CaptureRegion(1180, 0, 100, 100));
        Assert.IsTrue(zone.Zone.IsValid);

        screenCapture.CaptureScreen();
        Assert.AreEqual(2, zone.Writes);
    }

    [TestMethod]
    public void ZoneIsValidAgainWhenTheDisplayGetsLargeEnough()
    {
        using TestScreenCapture screenCapture = new();
        WriteCounter zone = new(screenCapture.RegisterCaptureZone(new CaptureRegion(1800, 0, 100, 100)));
        screenCapture.CaptureScreen();

        ChangeDisplay(screenCapture, SMALLER);
        screenCapture.CaptureScreen();
        Assert.IsFalse(zone.Zone.IsValid);

        ChangeDisplay(screenCapture, ORIGINAL);
        Assert.IsTrue(zone.Zone.IsValid);
        Assert.AreEqual(ORIGINAL, zone.Zone.Display);

        screenCapture.CaptureScreen();
        Assert.AreEqual(2, zone.Writes);
    }

    [TestMethod]
    public void ChangesBetweenCapturesAreRaisedOnce()
    {
        using TestScreenCapture screenCapture = new();
        List<ScreenCaptureDisplayChangedEventArgs> changes = [];
        screenCapture.DisplayChanged += (_, e) => changes.Add(e);

        ChangeDisplay(screenCapture, SMALLER);
        ChangeDisplay(screenCapture, ROTATED);
        screenCapture.CaptureScreen();

        Assert.HasCount(1, changes);
        Assert.AreEqual(new ScreenCaptureDisplayChangedEventArgs(ORIGINAL, ROTATED), changes[0]);

        // Changed and changed back: nothing to report.
        ChangeDisplay(screenCapture, SMALLER);
        ChangeDisplay(screenCapture, ROTATED);
        screenCapture.CaptureScreen();

        Assert.HasCount(1, changes);
    }

    [TestMethod]
    public void HandlerExceptionsAreIgnored()
    {
        using TestScreenCapture screenCapture = new();
        screenCapture.DisplayChanged += (_, _) => throw new InvalidOperationException("Handler failed.");

        ChangeDisplay(screenCapture, SMALLER);

        Assert.AreEqual(CaptureResult.NewFrame, screenCapture.CaptureScreen());
        Assert.IsNull(screenCapture.LastError);
    }

    [TestMethod]
    public void DisplayWithAnotherIdIsRejected()
    {
        using TestScreenCapture screenCapture = new();
        List<ScreenCaptureDisplayChangedEventArgs> changes = [];
        screenCapture.DisplayChanged += (_, e) => changes.Add(e);

        ChangeDisplay(screenCapture, SMALLER with { Id = "Other" });

        ArgumentException error = Assert.IsInstanceOfType<ArgumentException>(screenCapture.LastError);
        StringAssert.Contains(error.Message, "Other");
        Assert.AreEqual(ORIGINAL, screenCapture.Display);

        screenCapture.CaptureScreen();
        Assert.IsEmpty(changes);
    }

    /// <summary>
    /// A zone registered while another thread changes the display ends up with the current display: none is left with an old one.
    /// </summary>
    [TestMethod]
    public void ZonesRegisteredDuringChangesFollowTheDisplay()
    {
        const int ROUNDS = 50;
        const int ZONES_PER_ROUND = 200;

        for (int round = 0; round < ROUNDS; round++)
        {
            using TestScreenCapture screenCapture = new();

            using Barrier start = new(2);
            Thread registering = new(() =>
            {
                start.SignalAndWait();
                for (int i = 0; i < ZONES_PER_ROUND; i++)
                    screenCapture.RegisterCaptureZone(new CaptureRegion(0, 0, 16, 16));
            });
            registering.Start();

            start.SignalAndWait();
            for (int i = 0; registering.IsAlive; i++)
                ChangeDisplay(screenCapture, (i % 2) == 0 ? SMALLER : ORIGINAL);
            registering.Join();

            // The last change was applied by its restart: every zone has to follow it without another capture.
            Assert.HasCount(ZONES_PER_ROUND, screenCapture.Zones);
            foreach (CaptureZone<ColorARGB> zone in screenCapture.Zones)
                Assert.AreEqual(screenCapture.Display, zone.Display, $"Round {round}");
        }
    }

    /// <summary>
    /// Simulates a restart that finds the display changed, like a backend after a display mode change.
    /// </summary>
    private static void ChangeDisplay(TestScreenCapture screenCapture, Display display)
    {
        screenCapture.NextDisplay = display;
        screenCapture.Restart();
    }

    private static ColorARGB GetFirstPixel(CaptureZone<ColorARGB> zone)
    {
        using CaptureFrame<ColorARGB> frame = zone.AcquireFrame();
        return frame.Pixels[0];
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
