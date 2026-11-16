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
    public void SingleBufferedZoneCantBeLockedDuringItsUpdate()
    {
        using TestScreenCapture screenCapture = new();
        CaptureZone<ColorARGB> zone = screenCapture.RegisterCaptureZone(new CaptureRegion(0, 0, 16, 8), doubleBuffered: false);

        bool? lockedByOtherThread = null;
        screenCapture.ZoneUpdating = z => lockedByOtherThread = Task.Run(() => { using (z.Lock()) { } }).Wait(TimeSpan.FromMilliseconds(100));

        screenCapture.CaptureScreen();

        Assert.IsFalse(lockedByOtherThread, "Another thread could lock the zone during its update.");
    }

    [TestMethod]
    public void DoubleBufferedZoneShowsThePreviousPixelsDuringItsUpdate()
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
            using CaptureFrame<ColorARGB> frame = z.Lock();
            pixelDuringUpdate = frame.Image[1, 1];
        }).Wait(TIMEOUT);

        screenCapture.CaptureScreen();

        Assert.IsTrue(lockedByOtherThread, "Another thread couldn't lock the zone during its update.");
        Assert.AreEqual(TestScreenCapture.GetColorFromLocation(1, 1), pixelDuringUpdate, "The frame doesn't show the previous pixels.");
        using (CaptureFrame<ColorARGB> frame = zone.Lock())
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
            using (other.Lock())
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
    /// The deadlock case: a user thread holds the zone lock and changes the zone while the capture thread wants to update it.
    /// </summary>
    [TestMethod]
    public void ZoneCanBeChangedWhileHoldingItsLock()
    {
        using TestScreenCapture screenCapture = new();
        CaptureZone<ColorARGB> zone = screenCapture.RegisterCaptureZone(new CaptureRegion(0, 0, 16, 8));

        int i = 0;
        RunWhileCapturing(screenCapture, () =>
        {
            using (zone.Lock())
                screenCapture.UpdateCaptureZone(zone, zone.Region with { X = i++ % 10, Width = 16 + (i % 2) });

            using (zone.Lock())
                screenCapture.UnregisterCaptureZone(screenCapture.RegisterCaptureZone(new CaptureRegion(0, 0, 4, 4)));
        });
    }

    /// <summary>
    /// One thread captures continuously (every capture fills the zone with one color, different for each capture), another one resizes the zone.
    /// Every frame must show the pixels of exactly one capture in one size, and they must not change while it's held.
    /// </summary>
    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void FramesNeverTear(bool doubleBuffered)
    {
        using TestScreenCapture screenCapture = new();
        CaptureZone<ColorARGB> zone = screenCapture.RegisterCaptureZone(new CaptureRegion(0, 0, 16, 8), doubleBuffered: doubleBuffered);

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

            // Under load the other threads can get the zone lock late (single-buffered frames hold it), so after ITERATIONS frames
            // keep reading until both kinds of change were seen. Only a capture or resize thread starved for TIMEOUT fails the test.
            Stopwatch stopwatch = Stopwatch.StartNew();
            for (int i = 0; (i < ITERATIONS) || (((sizeChanges <= 1) || (contentChanges <= 1)) && (stopwatch.Elapsed < TIMEOUT)); i++)
                using (CaptureFrame<ColorARGB> frame = zone.Lock())
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
