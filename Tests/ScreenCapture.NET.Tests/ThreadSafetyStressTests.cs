using System.Collections.Concurrent;
using HPPH;

namespace ScreenCapture.NET.Tests;

/// <summary>
/// The thread-safety contract (see the remarks of <see cref="IScreenCapture"/> and <see cref="ICaptureZone"/>): every member is used from many threads at once,
/// also while holding frames and from event handlers, and the capture is disposed while all of it runs. Nothing may throw (except the documented exceptions),
/// deadlock, tear a frame or a property, or report an error.
/// </summary>
[TestClass]
public sealed class ThreadSafetyStressTests
{
    #region Constants

    private static readonly TimeSpan DURATION = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan TIMEOUT = TimeSpan.FromSeconds(30);

    private static readonly Display BIG = TestScreenCapture.CreateDisplay(1920, 1080);
    private static readonly Display SMALL = TestScreenCapture.CreateDisplay(600, 800, ImageRotation.Rotate90);

    // These fit both displays.
    private static readonly CaptureRegion[] REGIONS = [new(0, 0, 16, 8), new(100, 50, 64, 32), new(0, 0, 600, 800)];

    // Fits only the big display: zones with it are invalid while the display is small, and registering it fails then.
    private static readonly CaptureRegion BIG_ONLY = new(1000, 500, 128, 64);

    private static readonly int[] LEVELS = [0, 1, 3];

    #endregion

    #region Methods

    [TestMethod]
    public void EverythingAtOnce()
    {
        TestScreenCapture screenCapture = new();
        StressRun stress = new(screenCapture, TIMEOUT);

        HashSet<CaptureRegion> regions = [.. REGIONS, BIG_ONLY];
        HashSet<Display> displays = [BIG, SMALL];
        HashSet<(int Width, int Height)> sizes = [.. regions.SelectMany(r => LEVELS.Select(l => StressRun.ScaledSize(r, l)))];

        // Every zone write gets its own color, so a frame mixing two writes shows. Writes run under CaptureLock like a backend's.
        int writes = 0;
        ColorARGB color = default;
        screenCapture.ZoneUpdating = _ =>
        {
            int number = Interlocked.Increment(ref writes);
            color = new ColorARGB(255, (byte)number, (byte)(number >> 8), (byte)(number >> 16));
        };
        screenCapture.Pattern = (_, _) => color;

        CaptureZone<ColorARGB> first = screenCapture.RegisterCaptureZone(REGIONS[0]);
        CaptureZone<ColorARGB> second = screenCapture.RegisterCaptureZone(REGIONS[1], 1);
        CaptureZone<ColorARGB> bigOnly = screenCapture.RegisterCaptureZone(BIG_ONLY);
        CaptureZone<ColorARGB>[] zones = [first, second, bigOnly];

        ConcurrentQueue<Exception> errors = new();
        screenCapture.ErrorOccurred += (_, e) => errors.Enqueue(e.Exception);

        int displayChanges = 0;
        screenCapture.DisplayChanged += (_, _) => Interlocked.Increment(ref displayChanges);

        // Handlers use the capture and its zones too. The capture swallows their exceptions, so they're recorded here.
        screenCapture.Updated += (_, _) => first.RequestUpdate();
        second.Updated += (_, _) => stress.Check("zone handler", () => AssertFrame(first, sizes));

        using ScreenCaptureLoop loop = new(screenCapture);
        loop.Start();

        stress.Run("capture", i =>
        {
            // Holding a frame while capturing: the zone is written into its other buffer, or skips the capture.
            if ((i % 5) == 0)
                using (second.AcquireFrame())
                    screenCapture.CaptureScreen();
            else
                screenCapture.CaptureScreen();
        });

        stress.Run("update", i =>
        {
            CaptureZone<ColorARGB> zone = (i % 2) == 0 ? first : second;
            CaptureRegion region = REGIONS[i % REGIONS.Length];
            int level = LEVELS[(i / REGIONS.Length) % LEVELS.Length];

            if ((i % 7) == 0)
                using (zone.AcquireFrame())
                    screenCapture.UpdateCaptureZone(zone, region, level);
            else
                screenCapture.UpdateCaptureZone(zone, region, level);
        });

        stress.Run("restart", i =>
        {
            // Every restart finds the other display: zones follow it, bigOnly becomes invalid and valid again.
            screenCapture.NextDisplay = (i % 2) == 0 ? SMALL : BIG;
            screenCapture.Restart();
            Thread.Sleep(1);
        });

        stress.Run("register", i =>
        {
            CaptureRegion region = (i % 4) == 0 ? BIG_ONLY : REGIONS[i % REGIONS.Length];
            CaptureZone<ColorARGB> zone = screenCapture.RegisterCaptureZone(region, LEVELS[i % LEVELS.Length]);
            screenCapture.UnregisterCaptureZone(zone);
        }, ex => ex is ArgumentOutOfRangeException { ParamName: "region" }); // BIG_ONLY while the display is small.

        for (int reader = 0; reader < 2; reader++)
            stress.Run($"frames {reader}", _ =>
            {
                foreach (CaptureZone<ColorARGB> zone in zones)
                    AssertFrame(zone, sizes);
            });

        stress.Run("properties", i =>
        {
            Assert.Contains(screenCapture.Display, displays);

            foreach (CaptureZone<ColorARGB> zone in zones)
            {
                // Each property on its own is a value that was set; separate reads can belong to different changes.
                Assert.Contains(zone.Region, regions, "The region is a mix of two regions.");
                Assert.Contains(zone.Display, displays, "The display is a mix of two displays.");
                Assert.Contains(zone.DownscaleLevel, LEVELS);
                int width = zone.Width;
                int height = zone.Height;
                Assert.IsTrue(sizes.Any(s => s.Width == width), $"The width {width} doesn't belong to any state of the zone.");
                Assert.IsTrue(sizes.Any(s => s.Height == height), $"The height {height} doesn't belong to any state of the zone ({zone.Region}, level {zone.DownscaleLevel}).");
                _ = zone.IsValid;
                _ = zone.IsUpdateRequested;

                if ((i % 10) == 0)
                {
                    zone.AutoUpdate = !zone.AutoUpdate;
                    zone.RequestUpdate();
                }
            }
        });

        Thread.Sleep(DURATION);
        int writesBeforeDispose = Volatile.Read(ref writes);

        // Running calls finish, later ones throw ObjectDisposedException, which ends their workers.
        screenCapture.Dispose();
        Thread.Sleep(100);

        stress.StopAndAssert();

        Assert.IsTrue(loop.Completion.Wait(TIMEOUT), "The capture loop didn't end after the capture was disposed.");
        Assert.IsTrue(loop.Completion.IsCompletedSuccessfully);
        Assert.IsEmpty(errors, $"Errors: {string.Join(" | ", errors)}");

        // Makes sure the test actually raced.
        Assert.IsGreaterThan(100, writesBeforeDispose, "Hardly any zone was written.");
        Assert.IsGreaterThan(1, Volatile.Read(ref displayChanges), "The display never changed.");
    }

    /// <summary>
    /// Locks a frame and checks it: its size belongs to one state of the zone, its buffer matches that size, and all its pixels come from the same write.
    /// </summary>
    private static void AssertFrame(CaptureZone<ColorARGB> zone, HashSet<(int Width, int Height)> sizes)
    {
        using CaptureFrame<ColorARGB> frame = zone.AcquireFrame();

        Assert.Contains((frame.Width, frame.Height), sizes, "The frame has a size the zone never had.");
        Assert.AreEqual(frame.Width * ColorARGB.ColorFormat.BytesPerPixel, frame.Stride);
        Assert.AreEqual(frame.Stride * frame.Height, frame.RawBuffer.Length);

        ReadOnlySpan<ColorARGB> pixels = frame.Pixels;
        ColorARGB first = pixels[0];
        Thread.SpinWait(50);

        Assert.IsFalse(pixels.ContainsAnyExcept(first), "The frame contains pixels of different writes, or they changed while it was held.");
    }

    #endregion
}

/// <summary>
/// Runs actions over and over on their own threads until stopped, and records what they throw.
/// A worker ends when the capture is disposed and it gets an <see cref="ObjectDisposedException"/>.
/// </summary>
internal sealed class StressRun(IScreenCapture screenCapture, TimeSpan timeout)
{
    #region Properties & Fields

    private readonly List<(string Name, Task Task, Func<int> Iterations)> _workers = [];
    private readonly ConcurrentQueue<string> _failures = new();
    private volatile bool _stop;

    #endregion

    #region Methods

    /// <summary>
    /// Runs <paramref name="iteration"/> with an increasing number until <see cref="StopAndAssert"/> is called.
    /// </summary>
    /// <param name="name">The name of the worker in failures.</param>
    /// <param name="iteration">One iteration; it gets the number of the iteration.</param>
    /// <param name="allowed">Exceptions that are expected (the worker goes on).</param>
    public void Run(string name, Action<int> iteration, Func<Exception, bool>? allowed = null)
    {
        int completed = 0;
        Task task = Task.Factory.StartNew(() =>
        {
            for (int i = 0; !_stop; i++)
            {
                try
                {
                    iteration(i);
                    completed++;
                }
                catch (ObjectDisposedException) when (screenCapture.IsDisposed)
                {
                    return;
                }
                catch (Exception ex) when (allowed?.Invoke(ex) == true) { }
                catch (Exception ex)
                {
                    _failures.Enqueue($"{name}: {ex}");
                    return;
                }
            }
        }, TaskCreationOptions.LongRunning);

        _workers.Add((name, task, () => completed));
    }

    /// <summary>
    /// Runs a check (e.g. in an event handler, whose exceptions the capture swallows) and records its failure.
    /// </summary>
    public void Check(string name, Action check)
    {
        try
        {
            check();
        }
        catch (Exception ex)
        {
            _failures.Enqueue($"{name}: {ex}");
        }
    }

    /// <summary>
    /// Gets the size of the pixels of a zone with the given region and downscale level (like <c>AbstractScreenCapture.CalculateScaledSize</c>).
    /// </summary>
    public static (int Width, int Height) ScaledSize(CaptureRegion region, int downscaleLevel)
    {
        int width = region.Width;
        int height = region.Height;
        for (int i = 0; (i < downscaleLevel) && ((width > 1) || (height > 1)); i++)
        {
            width /= 2;
            height /= 2;
        }

        return (Math.Max(width, 1), Math.Max(height, 1));
    }

    /// <summary>
    /// Stops the workers and checks that they all ended, completed iterations and recorded no failures.
    /// </summary>
    public void StopAndAssert()
    {
        _stop = true;

        // One deadline for all of them, naming the ones that hang.
        Task.WaitAll([.. _workers.Select(w => w.Task)], timeout);
        string[] hanging = [.. _workers.Where(w => !w.Task.IsCompleted).Select(w => w.Name)];
        Assert.IsEmpty(hanging, $"Workers that didn't finish (deadlock?): {string.Join(", ", hanging)}");

        Assert.IsEmpty(_failures, string.Join(Environment.NewLine, _failures));

        foreach ((string name, _, Func<int> iterations) in _workers)
            Assert.IsGreaterThan(0, iterations(), $"The worker '{name}' never completed an iteration.");
    }

    #endregion
}
