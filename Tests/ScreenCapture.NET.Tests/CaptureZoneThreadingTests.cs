using System.Diagnostics;
using HPPH;

namespace ScreenCapture.NET.Tests;

/// <summary>
/// Zone set changes, zone changes and captures from different threads, and the lock order between zone locks and the screen capture.
/// </summary>
[TestClass]
public sealed class CaptureZoneThreadingTests
{
    #region Constants

    private static readonly TimeSpan TIMEOUT = TimeSpan.FromSeconds(10);
    private const int ITERATIONS = 2000;

    #endregion

    #region Methods

    [TestMethod]
    public void ZoneEventsAreRaisedAfterAllZonesAreUpdated()
    {
        using TestScreenCapture screenCapture = new();
        CaptureZone<ColorARGB> first = screenCapture.RegisterCaptureZone(new CaptureRegion(0, 0, 16, 8));
        CaptureZone<ColorARGB> second = screenCapture.RegisterCaptureZone(new CaptureRegion(16, 0, 16, 8));

        List<string> log = [];
        screenCapture.ZoneUpdating = zone => log.Add($"update {(zone == first ? "first" : "second")}");
        first.Updated += (_, _) => log.Add("event first");
        second.Updated += (_, _) => log.Add("event second");

        screenCapture.CaptureScreen();

        CollectionAssert.AreEqual(new[] { "update first", "update second", "event first", "event second" }, log);
    }

    [TestMethod]
    public void ZoneShowsThePreviousPixelsDuringItsUpdate()
    {
        using TestScreenCapture screenCapture = new();
        CaptureZone<ColorARGB> zone = screenCapture.RegisterCaptureZone(new CaptureRegion(0, 0, 16, 8));
        screenCapture.CaptureScreen();

        ColorARGB other = new(255, 1, 2, 3);
        screenCapture.Pattern = (_, _) => other;

        // Exceptions in zone updates are swallowed by the capture, so the results are checked afterwards.
        bool? lockedByOtherThread = null;
        ColorARGB? pixelDuringUpdate = null;
        screenCapture.ZoneUpdating = z => lockedByOtherThread = Task.Run(() =>
        {
            using CaptureFrame<ColorARGB> frame = z.AcquireFrame();
            pixelDuringUpdate = frame.Image[1, 1];
        }).Wait(TIMEOUT);

        screenCapture.CaptureScreen();

        Assert.IsTrue(lockedByOtherThread, "Another thread couldn't lock the zone during its update.");
        Assert.AreEqual(TestScreenCapture.GetColorFromLocation(1, 1), pixelDuringUpdate, "The frame doesn't show the previous pixels.");
        using (CaptureFrame<ColorARGB> frame = zone.AcquireFrame())
            Assert.AreEqual(other, frame.Image[1, 1], "The update wasn't shown afterwards.");
    }

    [TestMethod]
    public void HandlersCanChangeZones()
    {
        using TestScreenCapture screenCapture = new();
        CaptureZone<ColorARGB> zone = screenCapture.RegisterCaptureZone(new CaptureRegion(0, 0, 16, 8));
        CaptureZone<ColorARGB> other = screenCapture.RegisterCaptureZone(new CaptureRegion(0, 0, 16, 8));

        CaptureZone<ColorARGB>? added = null;
        zone.Updated += (_, _) =>
        {
            using (other.AcquireFrame())
                screenCapture.UpdateCaptureZone(other, other.Region with { X = 8 });

            added = screenCapture.RegisterCaptureZone(new CaptureRegion(0, 0, 4, 4));
            screenCapture.UnregisterCaptureZone(zone);
        };
        screenCapture.Updated += (_, _) => screenCapture.UnregisterCaptureZone(other);

        Assert.AreEqual(CaptureResult.NewFrame, screenCapture.CaptureScreen());

        Assert.IsNotNull(added);
        Assert.AreEqual(8, other.Region.X);
        Assert.IsFalse(screenCapture.UnregisterCaptureZone(zone));
        Assert.IsFalse(screenCapture.UnregisterCaptureZone(other));
        Assert.IsTrue(screenCapture.UnregisterCaptureZone(added));
    }

    [TestMethod]
    public void ZonesCanBeRegisteredAndUnregisteredDuringCaptures()
    {
        using TestScreenCapture screenCapture = new();

        RunWhileCapturing(screenCapture, () =>
        {
            CaptureZone<ColorARGB> zone = screenCapture.RegisterCaptureZone(new CaptureRegion(0, 0, 16, 8));
            Assert.IsTrue(screenCapture.UnregisterCaptureZone(zone));
        }, threads: 4);
    }

    /// <summary>
    /// A zone unregistered while a capture runs (after it took its snapshot) isn't written by that capture: backends never see unregistered zones.
    /// </summary>
    [TestMethod]
    public void ZoneUnregisteredDuringACaptureIsNotWritten()
    {
        using TestScreenCapture screenCapture = new();
        CaptureZone<ColorARGB> first = screenCapture.RegisterCaptureZone(new CaptureRegion(0, 0, 16, 8));
        CaptureZone<ColorARGB> second = screenCapture.RegisterCaptureZone(new CaptureRegion(16, 0, 16, 8));

        List<CaptureZone<ColorARGB>> written = [];
        screenCapture.ZoneUpdating = zone =>
        {
            written.Add(zone);
            if (zone == first)
                Assert.IsTrue(Task.Run(() => screenCapture.UnregisterCaptureZone(second)).Wait(TIMEOUT), "Unregistering a zone waited for the write of another one.");
        };

        int updates = 0;
        second.Updated += (_, _) => updates++;

        screenCapture.CaptureScreen();

        CollectionAssert.AreEqual(new[] { first }, written);
        Assert.AreEqual(0, updates);
    }

    /// <summary>
    /// Unregistering waits for a capture that writes the zone: once it returned, no capture writes it anymore (so a backend can release the zone's resources).
    /// </summary>
    [TestMethod]
    public void UnregisterWaitsForTheWriteOfTheZone()
    {
        using TestScreenCapture screenCapture = new();
        CaptureZone<ColorARGB> zone = screenCapture.RegisterCaptureZone(new CaptureRegion(0, 0, 16, 8));

        using ManualResetEventSlim writing = new();
        using ManualResetEventSlim release = new();
        screenCapture.ZoneUpdating = _ =>
        {
            writing.Set();
            release.Wait(TIMEOUT);
        };

        Task<CaptureResult> capture = Task.Run(screenCapture.CaptureScreen);
        Assert.IsTrue(writing.Wait(TIMEOUT));

        Task<bool> unregister = Task.Run(() => screenCapture.UnregisterCaptureZone(zone));
        Assert.IsFalse(unregister.Wait(100), "Unregistering didn't wait for the write of the zone.");

        release.Set();

        Assert.IsTrue(unregister.Wait(TIMEOUT));
        Assert.IsTrue(unregister.Result);
        Assert.IsTrue(capture.Wait(TIMEOUT));
    }

    /// <summary>
    /// The deadlock case: a user thread holds a frame of the zone and changes the zone while the capture thread wants to update it.
    /// </summary>
    [TestMethod]
    public void ZoneCanBeChangedWhileHoldingAFrame()
    {
        using TestScreenCapture screenCapture = new();
        CaptureZone<ColorARGB> zone = screenCapture.RegisterCaptureZone(new CaptureRegion(0, 0, 16, 8));

        int i = 0;
        RunWhileCapturing(screenCapture, () =>
        {
            using (zone.AcquireFrame())
                screenCapture.UpdateCaptureZone(zone, zone.Region with { X = i++ % 10, Width = 16 + (i % 2) });

            using (zone.AcquireFrame())
                screenCapture.UnregisterCaptureZone(screenCapture.RegisterCaptureZone(new CaptureRegion(0, 0, 4, 4)));
        });
    }

    /// <summary>
    /// One thread captures continuously (every capture fills the zone with one color, different for each capture), another one resizes the zone.
    /// Every frame must show the pixels of exactly one capture in one size, and they must not change while it's held.
    /// </summary>
    [TestMethod]
    public void FramesNeverTear()
    {
        using TestScreenCapture screenCapture = new();
        CaptureZone<ColorARGB> zone = screenCapture.RegisterCaptureZone(new CaptureRegion(0, 0, 16, 8));

        int captureNumber = 0;
        ColorARGB color = default;
        screenCapture.ZoneUpdating = _ =>
        {
            int number = ++captureNumber;
            color = new ColorARGB(255, (byte)number, (byte)(number >> 8), (byte)(number >> 16));
        };
        screenCapture.Pattern = (_, _) => color;
        screenCapture.CaptureScreen();

        using CancellationTokenSource cancellation = new();
        Task capture = Task.Factory.StartNew(() =>
        {
            while (!cancellation.IsCancellationRequested)
                screenCapture.CaptureScreen();
        }, TaskCreationOptions.LongRunning);
        Task resize = Task.Factory.StartNew(() =>
        {
            for (int i = 0; !cancellation.IsCancellationRequested; i++)
                screenCapture.UpdateCaptureZone(zone, zone.Region with { Width = 16 << (i % 2), Height = 8 << (i % 2) });
        }, TaskCreationOptions.LongRunning);

        try
        {
            int sizeChanges = 0;
            int contentChanges = 0;
            (int width, ColorARGB first) previous = default;

            // Under load the other threads can be scheduled late, so after ITERATIONS frames
            // keep reading until both kinds of change were seen. Only a capture or resize thread starved for TIMEOUT fails the test.
            Stopwatch stopwatch = Stopwatch.StartNew();
            for (int i = 0; (i < ITERATIONS) || (((sizeChanges <= 1) || (contentChanges <= 1)) && (stopwatch.Elapsed < TIMEOUT)); i++)
                using (CaptureFrame<ColorARGB> frame = zone.AcquireFrame())
                {
                    Assert.AreEqual(frame.Width * 2, frame.Height * 4, "Width and height don't belong to the same size.");
                    Assert.AreEqual(frame.Stride * frame.Height, frame.RawBuffer.Length);

                    ReadOnlySpan<ColorARGB> pixels = frame.Pixels;
                    ColorARGB first = pixels[0];
                    Thread.SpinWait(100);

                    Assert.IsFalse(pixels.ContainsAnyExcept(first), "The frame contains pixels of different captures.");

                    if (previous.width != frame.Width) sizeChanges++;
                    if (!previous.first.Equals(first)) contentChanges++;
                    previous = (frame.Width, first);
                }

            // Makes sure the test actually raced against captures and resizes.
            Assert.IsGreaterThan(1, sizeChanges, "The frames never changed their size.");
            Assert.IsGreaterThan(1, contentChanges, "The frames never changed their pixels.");
        }
        finally
        {
            cancellation.Cancel();
            Assert.IsTrue(capture.Wait(TIMEOUT), "The capture thread didn't finish.");
            Assert.IsTrue(resize.Wait(TIMEOUT), "The resize thread didn't finish.");
        }
    }

    /// <summary>
    /// The region and the display of a zone are structs that other threads read without a lock while they change: every read must give one of the values that were set.
    /// </summary>
    [TestMethod]
    public void ZonePropertiesNeverTear()
    {
        Display[] displays = [TestScreenCapture.CreateDisplay(1920, 1080), TestScreenCapture.CreateDisplay(2560, 1440, ImageRotation.Rotate180)];
        CaptureRegion[] regions = [new(0, 0, 10, 10), new(100, 200, 300, 400)];

        using TestScreenCapture screenCapture = new();
        CaptureZone<ColorARGB> zone = screenCapture.RegisterCaptureZone(regions[0]);

        using CancellationTokenSource cancellation = new();
        Task update = Task.Factory.StartNew(() =>
        {
            for (int i = 0; !cancellation.IsCancellationRequested; i++)
                screenCapture.UpdateCaptureZone(zone, regions[i % 2]);
        }, TaskCreationOptions.LongRunning);
        Task restart = Task.Factory.StartNew(() =>
        {
            // Every restart finds the other display; the zone follows it.
            for (int i = 1; !cancellation.IsCancellationRequested; i++)
            {
                screenCapture.NextDisplay = displays[i % 2];
                screenCapture.Restart();
            }
        }, TaskCreationOptions.LongRunning);

        try
        {
            int regionChanges = 0;
            int displayChanges = 0;
            (CaptureRegion region, Display display) previous = (zone.Region, zone.Display);

            Stopwatch stopwatch = Stopwatch.StartNew();
            for (int i = 0; (i < (ITERATIONS * 100)) || (((regionChanges <= 1) || (displayChanges <= 1)) && (stopwatch.Elapsed < TIMEOUT)); i++)
            {
                CaptureRegion region = zone.Region;
                Display display = zone.Display;

                Assert.Contains(region, regions, "The region is a mix of two regions.");
                Assert.Contains(display, displays, "The display is a mix of two displays.");

                // Both regions fit both displays.
                Assert.IsTrue(zone.IsValid);

                if (region != previous.region) regionChanges++;
                if (display != previous.display) displayChanges++;
                previous = (region, display);
            }

            // Makes sure the test actually raced against the changes.
            Assert.IsGreaterThan(1, regionChanges, "The region never changed.");
            Assert.IsGreaterThan(1, displayChanges, "The display never changed.");
        }
        finally
        {
            cancellation.Cancel();
            Assert.IsTrue(update.Wait(TIMEOUT), "The update thread didn't finish.");
            Assert.IsTrue(restart.Wait(TIMEOUT), "The restart thread didn't finish.");
        }
    }

    /// <summary>
    /// An update requested while the zone is written (e.g. from another thread) wants a newer frame than the one being written: it must not be cleared by that write.
    /// </summary>
    [TestMethod]
    public void UpdateRequestedDuringTheWriteIsKept()
    {
        using TestScreenCapture screenCapture = new();
        CaptureZone<ColorARGB> zone = screenCapture.RegisterCaptureZone(new CaptureRegion(0, 0, 16, 8));
        zone.AutoUpdate = false;
        zone.RequestUpdate();

        int writes = 0;
        screenCapture.ZoneUpdating = z =>
        {
            if (++writes == 1)
                z.RequestUpdate();
        };

        screenCapture.CaptureScreen();
        Assert.IsTrue(zone.IsUpdateRequested, "The request made during the write was cleared.");

        // The kept request is served by the next capture, also without a new frame.
        screenCapture.Result = CaptureResult.NoChange;
        screenCapture.CaptureScreen();
        Assert.AreEqual(2, writes);
        Assert.IsFalse(zone.IsUpdateRequested);
    }

    /// <summary>
    /// A request stays when the zone isn't written (here: the backend had nothing to write), so a later capture serves it.
    /// </summary>
    [TestMethod]
    public void UpdateRequestStaysWhenTheZoneIsntWritten()
    {
        using TestScreenCapture screenCapture = new() { WriteZones = false };
        CaptureZone<ColorARGB> zone = screenCapture.RegisterCaptureZone(new CaptureRegion(0, 0, 16, 8));
        zone.AutoUpdate = false;
        zone.RequestUpdate();

        screenCapture.CaptureScreen();
        Assert.IsTrue(zone.IsUpdateRequested);

        screenCapture.ZoneUpdating = _ => throw new InvalidOperationException("test");
        screenCapture.CaptureScreen();
        Assert.IsTrue(zone.IsUpdateRequested, "A failed write cleared the request.");

        screenCapture.ZoneUpdating = null;
        screenCapture.WriteZones = true;
        screenCapture.CaptureScreen();
        Assert.IsFalse(zone.IsUpdateRequested);
    }

    /// <summary>
    /// Runs <paramref name="action"/> <see cref="ITERATIONS"/> times on each of <paramref name="threads"/> threads while another thread captures continuously.
    /// Fails if anything throws or doesn't finish within <see cref="TIMEOUT"/> (a deadlock).
    /// </summary>
    private static void RunWhileCapturing(TestScreenCapture screenCapture, Action action, int threads = 1)
    {
        using CancellationTokenSource cancellation = new();
        Task capture = Task.Factory.StartNew(() =>
        {
            while (!cancellation.IsCancellationRequested)
                screenCapture.CaptureScreen();
        }, TaskCreationOptions.LongRunning);

        Task[] workers = [.. Enumerable.Range(0, threads).Select(_ => Task.Factory.StartNew(() =>
        {
            for (int i = 0; i < ITERATIONS; i++)
                action();
        }, TaskCreationOptions.LongRunning))];

        bool finished;
        try
        {
            finished = Task.WaitAll(workers, TIMEOUT);
        }
        finally
        {
            cancellation.Cancel();
        }

        Assert.IsTrue(finished, "The workers didn't finish (deadlock?).");
        Assert.IsTrue(capture.Wait(TIMEOUT), "The capture thread didn't finish (deadlock?).");
    }

    #endregion
}
