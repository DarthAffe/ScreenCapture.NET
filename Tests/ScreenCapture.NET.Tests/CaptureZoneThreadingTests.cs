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
        CaptureZone<ColorARGB> first = screenCapture.RegisterCaptureZone(0, 0, 16, 8);
        CaptureZone<ColorARGB> second = screenCapture.RegisterCaptureZone(16, 0, 16, 8);

        List<string> log = [];
        screenCapture.ZoneUpdating = zone => log.Add($"update {(zone == first ? "first" : "second")}");
        first.Updated += (_, _) => log.Add("event first");
        second.Updated += (_, _) => log.Add("event second");

        screenCapture.CaptureScreen();

        CollectionAssert.AreEqual(new[] { "update first", "update second", "event first", "event second" }, log);
    }

    [TestMethod]
    public void ZoneIsUpdatedWithItsLockHeld()
    {
        using TestScreenCapture screenCapture = new();
        CaptureZone<ColorARGB> zone = screenCapture.RegisterCaptureZone(0, 0, 16, 8);

        bool? lockedByOtherThread = null;
        screenCapture.ZoneUpdating = z => lockedByOtherThread = !Task.Run(() => { using (z.Lock()) { } }).Wait(TimeSpan.FromMilliseconds(100));

        screenCapture.CaptureScreen();

        Assert.IsTrue(lockedByOtherThread, "Another thread could lock the zone during its update.");
    }

    [TestMethod]
    public void HandlersCanChangeZones()
    {
        using TestScreenCapture screenCapture = new();
        CaptureZone<ColorARGB> zone = screenCapture.RegisterCaptureZone(0, 0, 16, 8);
        CaptureZone<ColorARGB> other = screenCapture.RegisterCaptureZone(0, 0, 16, 8);

        CaptureZone<ColorARGB>? added = null;
        zone.Updated += (_, _) =>
        {
            using (other.Lock())
                screenCapture.UpdateCaptureZone(other, x: 8);

            added = screenCapture.RegisterCaptureZone(0, 0, 4, 4);
            screenCapture.UnregisterCaptureZone(zone);
        };
        screenCapture.Updated += (_, _) => screenCapture.UnregisterCaptureZone(other);

        Assert.IsTrue(screenCapture.CaptureScreen());

        Assert.IsNotNull(added);
        Assert.AreEqual(8, other.X);
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
            CaptureZone<ColorARGB> zone = screenCapture.RegisterCaptureZone(0, 0, 16, 8);
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
        CaptureZone<ColorARGB> zone = screenCapture.RegisterCaptureZone(0, 0, 16, 8);

        int i = 0;
        RunWhileCapturing(screenCapture, () =>
        {
            using (zone.Lock())
                screenCapture.UpdateCaptureZone(zone, x: i++ % 10, width: 16 + (i % 2));

            using (zone.Lock())
                screenCapture.UnregisterCaptureZone(screenCapture.RegisterCaptureZone(0, 0, 4, 4));
        });
    }

    [TestMethod]
    public void ResizeDoesNotChangeALockedZone()
    {
        using TestScreenCapture screenCapture = new();
        CaptureZone<ColorARGB> zone = screenCapture.RegisterCaptureZone(0, 0, 16, 8);

        using CancellationTokenSource cancellation = new();
        Task resize = Task.Run(() =>
        {
            for (int i = 0; !cancellation.IsCancellationRequested; i++)
                screenCapture.UpdateCaptureZone(zone, width: 16 << (i % 2), height: 8 << (i % 2));
        });

        try
        {
            for (int i = 0; i < ITERATIONS; i++)
                using (zone.Lock())
                {
                    int width = zone.Width;
                    int height = zone.Height;
                    int length = zone.RawBuffer.Length;
                    Thread.SpinWait(100);

                    Assert.AreEqual(width * 2, height * 4, "Width and height don't belong to the same size.");
                    Assert.AreEqual((width, height, length), (zone.Width, zone.Height, zone.RawBuffer.Length), "The zone changed while it was locked.");
                    Assert.AreEqual(zone.Stride * zone.Height, length);
                }
        }
        finally
        {
            cancellation.Cancel();
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
