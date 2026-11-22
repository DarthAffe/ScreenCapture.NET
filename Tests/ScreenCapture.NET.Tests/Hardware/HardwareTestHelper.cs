using HPPH;

namespace ScreenCapture.NET.Tests.Hardware;

/// <summary>
/// Shared checks for backend smoke tests. They need a real GPU/display and are excluded from CI ("Hardware", and "Interactive", see <see cref="INTERACTIVE_CATEGORY"/>).
/// </summary>
internal static class HardwareTestHelper
{
    public const string CATEGORY = "Hardware";

    /// <summary>
    /// Tests that take over the desktop (e.g. move the mouse pointer). Only run on request, never in CI.
    /// </summary>
    public const string INTERACTIVE_CATEGORY = "Interactive";

    private const int CAPTURE_ATTEMPTS = 20;

    /// <summary>
    /// A display that doesn't exist on any backend, so every restart fails.
    /// </summary>
    public static Display CreateMissingDisplay(int index = 99)
        => new("missing", index, "missing", string.Empty, 0, 0, 640, 480, ImageRotation.None, new GraphicsCard("first", 0, "first", 0, 0));

    public static (GraphicsCard graphicsCard, Display display) GetFirstDisplay(IScreenCaptureService service)
    {
        foreach (GraphicsCard graphicsCard in service.GetGraphicsCards())
            foreach (Display display in service.GetDisplays(graphicsCard))
                return (graphicsCard, display);

        Assert.Inconclusive("No display found.");
        return default;
    }

    public static void AssertEnumeratesDisplays(IScreenCaptureService service)
    {
        List<GraphicsCard> graphicsCards = [.. service.GetGraphicsCards()];
        Assert.IsNotEmpty(graphicsCards, "No graphics card found.");

        List<Display> displays = [.. graphicsCards.SelectMany(service.GetDisplays)];
        Assert.IsNotEmpty(displays, "No display found.");

        foreach (Display display in displays)
        {
            Assert.IsGreaterThan(0, display.Width, $"{display.DeviceName}: width");
            Assert.IsGreaterThan(0, display.Height, $"{display.DeviceName}: height");
        }
    }

    public static void AssertCapturesZone(IScreenCaptureService service, Func<Display, (int x, int y, int width, int height)> region, int downscaleLevel = 0)
    {
        (_, Display display) = GetFirstDisplay(service);
        IScreenCapture screenCapture = service.GetScreenCapture(display);

        (int x, int y, int width, int height) = region(display);
        ICaptureZone zone = screenCapture.RegisterCaptureZone(new CaptureRegion(x, y, width, height), downscaleLevel);

        int updates = 0;
        zone.Updated += (_, _) => updates++;

        // The first frame after (re)starting a capture can take a few attempts, e.g. if the desktop doesn't change.
        bool captured = false;
        for (int i = 0; (i < CAPTURE_ATTEMPTS) && !captured; i++)
            captured = screenCapture.CaptureScreen() == CaptureResult.NewFrame;

        Assert.IsTrue(captured, $"No frame captured after {CAPTURE_ATTEMPTS} attempts.");
        Assert.IsGreaterThan(0, updates, "Zone wasn't updated.");

        Assert.AreEqual(width >> zone.DownscaleLevel, zone.Width);
        Assert.AreEqual(height >> zone.DownscaleLevel, zone.Height);

        using (CaptureFrame frame = zone.AcquireFrame())
        {
            Assert.AreEqual(frame.Stride * frame.Height, frame.RawBuffer.Length);
            Assert.IsTrue(frame.RawBuffer.ContainsAnyExcept((byte)0), "Captured zone contains only zeros.");
        }

        Assert.IsTrue(screenCapture.UnregisterCaptureZone(zone));
    }

    /// <summary>
    /// Moves a zone (position only) and checks that it captures the same pixels as a zone registered at the new position.
    /// Both are filled from the same frame in the same <see cref="IScreenCapture.CaptureScreen"/> call.
    /// </summary>
    public static void AssertMovedZoneCapturesNewPosition(IScreenCaptureService service, int downscaleLevel = 0)
    {
        (_, Display display) = GetFirstDisplay(service);
        IScreenCapture screenCapture = service.GetScreenCapture(display);

        const int size = 64;
        (int centerX, int centerY) = ((display.Width - size) / 2, (display.Height - size) / 2);

        ICaptureZone moved = screenCapture.RegisterCaptureZone(new CaptureRegion(0, 0, size, size), downscaleLevel);
        CaptureFrame(screenCapture);

        screenCapture.UpdateCaptureZone(moved, moved.Region with { X = centerX, Y = centerY });
        ICaptureZone reference = screenCapture.RegisterCaptureZone(new CaptureRegion(centerX, centerY, size, size), downscaleLevel);
        ICaptureZone topLeft = screenCapture.RegisterCaptureZone(new CaptureRegion(0, 0, size, size), downscaleLevel);

        // No new frame needed, the zones are updated from the last one.
        screenCapture.CaptureScreen();

        byte[] movedPixels;
        byte[] referencePixels;
        byte[] topLeftPixels;
        using (CaptureFrame frame = moved.AcquireFrame()) movedPixels = frame.RawBuffer.ToArray();
        using (CaptureFrame frame = reference.AcquireFrame()) referencePixels = frame.RawBuffer.ToArray();
        using (CaptureFrame frame = topLeft.AcquireFrame()) topLeftPixels = frame.RawBuffer.ToArray();

        screenCapture.UnregisterCaptureZone(moved);
        screenCapture.UnregisterCaptureZone(reference);
        screenCapture.UnregisterCaptureZone(topLeft);

        if (topLeftPixels.AsSpan().SequenceEqual(referencePixels))
            Assert.Inconclusive("The top-left corner and the center of the screen look the same, so a move can't be detected.");

        Assert.IsTrue(movedPixels.AsSpan().SequenceEqual(referencePixels), "The moved zone doesn't show the pixels at its new position.");
    }

    /// <summary>
    /// Changes, registers and unregisters zones while holding a zone lock and another thread captures.
    /// This used to deadlock on the lock order.
    /// </summary>
    public static void AssertZonesCanBeChangedWhileCapturing(IScreenCaptureService service, int downscaleLevel = 0)
    {
        const int iterations = 200;
        TimeSpan timeout = TimeSpan.FromSeconds(30);

        (_, Display display) = GetFirstDisplay(service);
        IScreenCapture screenCapture = service.GetScreenCapture(display);
        ICaptureZone zone = screenCapture.RegisterCaptureZone(new CaptureRegion(0, 0, 64, 64), downscaleLevel);
        CaptureFrame(screenCapture);

        using CancellationTokenSource cancellation = new();
        int captures = 0;
        Task capture = Task.Factory.StartNew(() =>
        {
            while (!cancellation.IsCancellationRequested)
            {
                screenCapture.CaptureScreen();
                captures++;
            }
        }, TaskCreationOptions.LongRunning);

        Task worker = Task.Factory.StartNew(() =>
        {
            for (int i = 0; i < iterations; i++)
            {
                using (zone.AcquireFrame())
                    screenCapture.UpdateCaptureZone(zone, zone.Region with { X = i % 100, Width = 64 + (i % 2) });

                using (zone.AcquireFrame())
                    screenCapture.UnregisterCaptureZone(screenCapture.RegisterCaptureZone(new CaptureRegion(0, 0, 32, 32), downscaleLevel));
            }
        }, TaskCreationOptions.LongRunning);

        bool finished;
        try
        {
            finished = worker.Wait(timeout);
        }
        finally
        {
            cancellation.Cancel();
        }

        Assert.IsTrue(finished, "The worker didn't finish (deadlock?).");
        Assert.IsTrue(capture.Wait(timeout), "The capture thread didn't finish (deadlock?).");
        Assert.IsGreaterThan(0, captures, "No capture ran while the zones were changed.");

        screenCapture.UnregisterCaptureZone(zone);
    }

    /// <summary>
    /// Checks on every display that partial zones show the same pixels as the same area of a full-screen zone (also on rotated displays).
    /// </summary>
    public static void AssertPartialZonesMatchFullScreen(IScreenCaptureService service)
    {
        int checkedZones = 0;
        foreach (Display display in GetAllDisplays(service))
        {
            IScreenCapture screenCapture = service.GetScreenCapture(display);
            (int width, int height) = (display.Width, display.Height);

            // Asymmetric zones near the edges, so a mirrored source region shows something else.
            (int x, int y, int width, int height)[] regions =
            [
                (width / 8, height / 6, width / 3, height / 4),
                (width - 200, 0, 200, 100),
                (0, height - 120, 150, 120),
                (width / 2, height / 3, 1, height / 2)
            ];

            ICaptureZone fullScreen = screenCapture.RegisterCaptureZone(new CaptureRegion(0, 0, width, height));
            ICaptureZone[] zones = [.. regions.Select(r => screenCapture.RegisterCaptureZone(new CaptureRegion(r.x, r.y, r.width, r.height)))];

            CaptureFrame(screenCapture);

            ColorBGRA[] fullScreenPixels = GetPixels(fullScreen);
            foreach ((ICaptureZone zone, (int x, int y, int width, int height) region) in zones.Zip(regions))
            {
                ColorBGRA[] expected = Crop(fullScreenPixels, width, region.x, region.y, region.width, region.height);
                ColorBGRA[] mirrored = Crop(fullScreenPixels, width, width - region.x - region.width, height - region.y - region.height, region.width, region.height);

                CollectionAssert.AreEqual(expected, GetPixels(zone), $"{display.DeviceName} ({display.Rotation}): zone {region} differs from the full screen.");

                if (!expected.AsSpan().SequenceEqual(mirrored))
                    checkedZones++;
            }

            screenCapture.UnregisterCaptureZone(fullScreen);
            foreach (ICaptureZone zone in zones)
                screenCapture.UnregisterCaptureZone(zone);
        }

        if (checkedZones == 0)
            Assert.Inconclusive("Every zone looks like its mirrored counterpart, so a wrongly mirrored zone can't be detected.");
    }

    /// <summary>
    /// Captures every display with both services and checks that the images have the same orientation.
    /// <paramref name="reference"/> has to capture in display orientation (like DX9).
    /// </summary>
    /// <remarks>
    /// The captures are taken one after the other, so it compares the brightness of a coarse grid: the image has to be much closer to the reference
    /// than to it rotated by 180° (what mixing up 90° and 270° would give).
    /// </remarks>
    /// <summary>
    /// Downscaled zones of two backends nearly match, e.g. the CPU downscaling and DX11's mip chain (rounded per level).
    /// </summary>
    /// <remarks>
    /// The captures are taken one after the other; unscaled zones show if the screen changed in between. The first display whose center stood still is compared.
    /// </remarks>
    public static void AssertDownscaledZonesMatchReference(IScreenCaptureService service, IScreenCaptureService reference, int downscaleLevel)
    {
        // Average difference per channel (0-255). Different rounding gives far below 1 (measured 0.01 at level 1, 0.03 at level 3).
        const double tolerance = 1.0;
        const double maxUnscaledDifference = 0.1;
        const int size = 512;

        List<Display> referenceDisplays = [.. GetAllDisplays(reference)];
        List<string> skipped = [];
        foreach (Display display in GetAllDisplays(service))
        {
            Display referenceDisplay = referenceDisplays.FirstOrDefault(d => d.DeviceName == display.DeviceName);
            if (referenceDisplay.DeviceName == null)
            {
                skipped.Add($"{display.DeviceName}: not in the reference.");
                continue;
            }

            CaptureRegion region = new((display.Width - size) / 2, (display.Height - size) / 2, size, size);

            IScreenCapture screenCapture = service.GetScreenCapture(display);
            IScreenCapture referenceCapture = reference.GetScreenCapture(referenceDisplay);
            ICaptureZone unscaled = screenCapture.RegisterCaptureZone(region);
            ICaptureZone downscaled = screenCapture.RegisterCaptureZone(region, downscaleLevel);
            ICaptureZone referenceUnscaled = referenceCapture.RegisterCaptureZone(region);
            ICaptureZone referenceDownscaled = referenceCapture.RegisterCaptureZone(region, downscaleLevel);

            CaptureFrame(screenCapture);
            CaptureFrame(referenceCapture);

            double unscaledDifference = AverageDifference(GetPixels(unscaled), GetPixels(referenceUnscaled));
            if (unscaledDifference > maxUnscaledDifference)
            {
                skipped.Add($"{display.DeviceName}: the screen changed between the two captures (unscaled difference {unscaledDifference:F2}).");
                continue;
            }

            Assert.AreEqual((referenceDownscaled.Width, referenceDownscaled.Height), (downscaled.Width, downscaled.Height));
            double difference = AverageDifference(GetPixels(downscaled), GetPixels(referenceDownscaled));
            Assert.IsLessThan(tolerance, difference, $"{display.DeviceName}, level {downscaleLevel}: the downscaled zones differ by {difference:F2} on average (unscaled: {unscaledDifference:F2}).");
            return;
        }

        Assert.Inconclusive(string.Join(" ", skipped));
    }

    // Average absolute difference of the color channels (not alpha: DX9 and DX11 may leave it differently).
    private static double AverageDifference(ColorBGRA[] pixels, ColorBGRA[] reference)
    {
        Assert.HasCount(reference.Length, pixels);

        long sum = 0;
        for (int i = 0; i < pixels.Length; i++)
            sum += Math.Abs(pixels[i].B - reference[i].B) + Math.Abs(pixels[i].G - reference[i].G) + Math.Abs(pixels[i].R - reference[i].R);

        return sum / (pixels.Length * 3.0);
    }

    public static void AssertMatchesReferenceOrientation(IScreenCaptureService service, IScreenCaptureService reference)
    {
        // Below this the screen looks too much like itself rotated by 180°.
        const double minRotatedDistance = 4;

        List<Display> referenceDisplays = [.. GetAllDisplays(reference)];
        int comparedDisplays = 0;
        foreach (Display display in GetAllDisplays(service))
        {
            int referenceIndex = referenceDisplays.FindIndex(d => d.DeviceName == display.DeviceName);
            if (referenceIndex < 0) continue;
            Display referenceDisplay = referenceDisplays[referenceIndex];

            Assert.AreEqual((display.Width, display.Height), (referenceDisplay.Width, referenceDisplay.Height),
                            $"{display.DeviceName} ({display.Rotation}): the size differs from the reference.");

            double[] grid = BrightnessGrid(CaptureFullScreen(service, display), display.Width, display.Height);
            double[] referenceGrid = BrightnessGrid(CaptureFullScreen(reference, referenceDisplay), display.Width, display.Height);

            if (Distance([.. referenceGrid.Reverse()], referenceGrid) < minRotatedDistance) continue;

            double distance = Distance(grid, referenceGrid);
            double rotatedDistance = Distance([.. grid.Reverse()], referenceGrid);

            Assert.IsLessThan(rotatedDistance / 2, distance,
                              $"{display.DeviceName} ({display.Rotation}): the capture differs from the reference by {distance:F1} (rotated by 180°: {rotatedDistance:F1}).");

            comparedDisplays++;
        }

        if (comparedDisplays == 0)
            Assert.Inconclusive("No display could be compared (no matching reference display, or the screen looks the same when rotated by 180°).");
    }

    private static double[] BrightnessGrid(ColorBGRA[] pixels, int width, int height)
    {
        const int size = 16;

        double[] grid = new double[size * size];
        int[] counts = new int[size * size];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                ColorBGRA pixel = pixels[(y * width) + x];
                int cell = (((y * size) / height) * size) + ((x * size) / width);
                grid[cell] += (pixel.R + pixel.G + pixel.B) / 3.0;
                counts[cell]++;
            }

        for (int i = 0; i < grid.Length; i++)
            grid[i] /= counts[i];

        return grid;
    }

    private static double Distance(double[] grid, double[] other)
    {
        double sum = 0;
        for (int i = 0; i < grid.Length; i++)
            sum += Math.Abs(grid[i] - other[i]);

        return sum / grid.Length;
    }

    public static IEnumerable<Display> GetAllDisplays(IScreenCaptureService service)
        => service.GetGraphicsCards().SelectMany(service.GetDisplays);

    private static ColorBGRA[] CaptureFullScreen(IScreenCaptureService service, Display display)
    {
        IScreenCapture screenCapture = service.GetScreenCapture(display);
        ICaptureZone zone = screenCapture.RegisterCaptureZone(new CaptureRegion(0, 0, display.Width, display.Height));

        CaptureFrame(screenCapture);
        ColorBGRA[] pixels = GetPixels(zone);

        screenCapture.UnregisterCaptureZone(zone);
        return pixels;
    }

    public static ColorBGRA[] GetPixels(ICaptureZone zone)
    {
        using CaptureFrame frame = zone.AcquireFrame();
        Assert.AreEqual(ColorBGRA.ColorFormat, frame.ColorFormat, "Only BGRA zones are supported.");
        return frame.AsRefImage<ColorBGRA>().ToArray();
    }

    public static ColorBGRA[] Crop(ColorBGRA[] pixels, int stride, int x, int y, int width, int height)
    {
        ColorBGRA[] result = new ColorBGRA[width * height];
        for (int row = 0; row < height; row++)
            pixels.AsSpan(((y + row) * stride) + x, width).CopyTo(result.AsSpan(row * width));

        return result;
    }

    public static void CaptureFrame(IScreenCapture screenCapture)
    {
        bool captured = false;
        for (int i = 0; (i < CAPTURE_ATTEMPTS) && !captured; i++)
            captured = screenCapture.CaptureScreen() == CaptureResult.NewFrame;

        Assert.IsTrue(captured, $"No frame captured after {CAPTURE_ATTEMPTS} attempts.");
    }

    /// <summary>
    /// A capture created with an outdated display finds it by its id and reports the change with its first capture,
    /// like a restart after a mode change, without changing the mode.
    /// </summary>
    public static void AssertOutdatedDisplayIsReadAgain(Display current, Display outdated, Func<Display, IScreenCapture> createCapture)
    {
        Assert.AreEqual(current.Id, outdated.Id);
        Assert.AreNotEqual(current, outdated);

        using IScreenCapture screenCapture = createCapture(outdated);
        Assert.IsNull(screenCapture.LastError, $"The capture didn't start: {screenCapture.LastError}");
        Assert.AreEqual(current, screenCapture.Display);

        List<ScreenCaptureDisplayChangedEventArgs> changes = [];
        screenCapture.DisplayChanged += (_, e) => changes.Add(e);

        // Validated against the current display: it doesn't fit the outdated one.
        ICaptureZone zone = screenCapture.RegisterCaptureZone(new CaptureRegion(0, 0, current.Width, current.Height));
        CaptureFrame(screenCapture);

        Assert.HasCount(1, changes);
        Assert.AreEqual(new ScreenCaptureDisplayChangedEventArgs(outdated, current), changes[0]);
        Assert.AreEqual(current, zone.Display);
        Assert.IsTrue(zone.IsValid);

        // Any result will do, the change is applied after every capture.
        screenCapture.CaptureScreen();
        Assert.HasCount(1, changes);
    }

    /// <summary>
    /// A restart between a capture getting its frame and writing the zones (here from <see cref="IScreenCapture.DisplayChanged"/>, which runs exactly then)
    /// leaves the backend without a frame: the zones must not be written from it.
    /// </summary>
    /// <returns><c>false</c> if the first capture never had a new frame, so nothing was checked.</returns>
    public static bool TryAssertRestartDuringACaptureWritesNoEmptyZone(Display current, Display outdated, Func<Display, IScreenCapture> createCapture)
    {
        // A new DX11 duplication has no new frame if nothing is presented meanwhile.
        for (int attempt = 0; attempt < 5; attempt++)
        {
            using IScreenCapture screenCapture = createCapture(outdated);
            ICaptureZone zone = screenCapture.RegisterCaptureZone(new CaptureRegion(0, 0, Math.Min(64, current.Width), Math.Min(64, current.Height)));

            int writes = 0;
            zone.Updated += (_, _) => writes++;
            screenCapture.DisplayChanged += (_, _) => screenCapture.Restart();

            CaptureResult result = screenCapture.CaptureScreen();
            if (result != CaptureResult.NewFrame) continue;

            Assert.AreEqual(0, writes, "The zone was written from the resources of the restarted backend, which hold no frame yet.");

            // The next frame writes it.
            CaptureFrame(screenCapture);
            Assert.AreEqual(1, writes);
            Assert.IsTrue(GetPixels(zone).Any(p => !p.Equals(default(ColorBGRA))), "The zone is empty.");
            return true;
        }

        return false;
    }

    /// <summary>
    /// A zone written with earlier frames shows the current one, like a zone registered now for the same region.
    /// DX11 keeps a copy of each zone on the GPU, one from an older frame must not be used.
    /// </summary>
    /// <remarks>
    /// Only detects a stale zone if the screen changes between the frames, a static screen passes.
    /// </remarks>
    public static void AssertZoneShowsTheLatestFrame(IScreenCaptureService service)
    {
        (_, Display display) = GetFirstDisplay(service);
        IScreenCapture screenCapture = service.GetScreenCapture(display);

        CaptureRegion fullScreen = new(0, 0, display.Width, display.Height);
        ICaptureZone kept = screenCapture.RegisterCaptureZone(fullScreen, downscaleLevel: 2);
        CaptureFrame(screenCapture);

        for (int i = 0; i < 5; i++)
        {
            // Written by the next capture, from the same frame as kept.
            ICaptureZone current = screenCapture.RegisterCaptureZone(fullScreen, downscaleLevel: 2);
            CaptureResult result = screenCapture.CaptureScreen();
            if (result == CaptureResult.NewFrame)
                Assert.IsTrue(GetPixels(kept).AsSpan().SequenceEqual(GetPixels(current)), $"The zone doesn't show the latest frame (round {i}).");

            screenCapture.UnregisterCaptureZone(current);
        }

        screenCapture.UnregisterCaptureZone(kept);
    }

    /// <summary>
    /// Several threads capture the same display at once while zones are read: no errors, only results of a working capture, and zones with pixels.
    /// </summary>
    public static void AssertConcurrentCapturesWork(IScreenCaptureService service)
    {
        const int threads = 4;
        TimeSpan duration = TimeSpan.FromSeconds(2);

        (_, Display display) = GetFirstDisplay(service);
        IScreenCapture screenCapture = service.GetScreenCapture(display);

        ICaptureZone[] zones =
        [
            screenCapture.RegisterCaptureZone(new CaptureRegion(0, 0, display.Width, display.Height)),
            screenCapture.RegisterCaptureZone(new CaptureRegion(display.Width / 4, display.Height / 4, display.Width / 2, display.Height / 2), downscaleLevel: 2),
            screenCapture.RegisterCaptureZone(new CaptureRegion(0, 0, 64, 64))
        ];

        System.Collections.Concurrent.ConcurrentQueue<Exception> errors = new();
        screenCapture.ErrorOccurred += (_, e) => errors.Enqueue(e.Exception);
        CaptureFrame(screenCapture);

        System.Collections.Concurrent.ConcurrentDictionary<CaptureResult, int> results = new();
        using CancellationTokenSource cancellation = new(duration);
        Task[] captures = [.. Enumerable.Range(0, threads).Select(_ => Task.Factory.StartNew(() =>
        {
            while (!cancellation.IsCancellationRequested)
                results.AddOrUpdate(screenCapture.CaptureScreen(), 1, (_, count) => count + 1);
        }, TaskCreationOptions.LongRunning))];

        while (!cancellation.IsCancellationRequested)
            foreach (ICaptureZone zone in zones)
                GetPixels(zone);

        Assert.IsTrue(Task.WaitAll(captures, TimeSpan.FromSeconds(30)), "The captures didn't finish (deadlock?).");

        Assert.IsEmpty(errors, $"Errors: {string.Join(" | ", errors)}");
        Assert.IsTrue(results.Keys.All(r => r is CaptureResult.NewFrame or CaptureResult.NoChange or CaptureResult.Timeout), $"Results: {string.Join(", ", results)}");
        Assert.IsTrue(results.ContainsKey(CaptureResult.NewFrame), $"No new frame: {string.Join(", ", results)}");

        foreach (ICaptureZone zone in zones)
            Assert.IsTrue(GetPixels(zone).Any(p => !p.Equals(default(ColorBGRA))), $"The zone {zone.Region} is empty.");
    }

    /// <summary>
    /// The thread-safety contract on a real backend (see <see cref="ThreadSafetyStressTests"/> for exact pixel checks): captures, zone changes, restarts,
    /// frames and properties on several threads, and the service disposed meanwhile. Nothing may throw (except <see cref="ObjectDisposedException"/> after the dispose),
    /// deadlock or report an error, and every frame must have a size the zone had.
    /// </summary>
    public static void AssertThreadSafetyContract(IScreenCaptureService service)
    {
        TimeSpan duration = TimeSpan.FromSeconds(3);
        TimeSpan timeout = TimeSpan.FromSeconds(30);
        int[] levels = [0, 1, 3];

        (_, Display display) = GetFirstDisplay(service);
        IScreenCapture screenCapture = service.GetScreenCapture(display);
        StressRun stress = new(screenCapture, timeout);

        CaptureRegion[] regions =
        [
            new(0, 0, display.Width, display.Height),
            new(display.Width / 4, display.Height / 4, display.Width / 2, display.Height / 2),
            new(0, 0, 64, 64)
        ];
        HashSet<CaptureRegion> regionSet = [.. regions];
        HashSet<(int Width, int Height)> sizes = [.. regions.SelectMany(r => levels.Select(l => StressRun.ScaledSize(r, l)))];

        ICaptureZone first = screenCapture.RegisterCaptureZone(regions[0]);
        ICaptureZone second = screenCapture.RegisterCaptureZone(regions[1], 1);
        ICaptureZone small = screenCapture.RegisterCaptureZone(regions[2], 3);
        ICaptureZone[] zones = [first, second, small];

        System.Collections.Concurrent.ConcurrentQueue<Exception> errors = new();
        screenCapture.ErrorOccurred += (_, e) => errors.Enqueue(e.Exception);

        System.Collections.Concurrent.ConcurrentDictionary<CaptureResult, int> results = new();
        screenCapture.Updated += (_, e) => results.AddOrUpdate(e.Result, 1, (_, count) => count + 1);
        second.Updated += (_, _) => stress.Check("zone handler", () => AssertFrameSize(first, sizes));

        CaptureFrame(screenCapture);

        // Not disposed by the test, which would wait after a deadlock. Disposing the capture ends it.
        ScreenCaptureLoop loop = new(screenCapture);
        loop.Start();

        stress.Run("capture", i =>
        {
            if ((i % 5) == 0)
                using (second.AcquireFrame())
                    screenCapture.CaptureScreen();
            else
                screenCapture.CaptureScreen();
        });

        stress.Run("update", i =>
        {
            ICaptureZone zone = (i % 2) == 0 ? first : second;
            CaptureRegion region = regions[i % regions.Length];
            int level = levels[(i / regions.Length) % levels.Length];

            if ((i % 7) == 0)
                using (zone.AcquireFrame())
                    screenCapture.UpdateCaptureZone(zone, region, level);
            else
                screenCapture.UpdateCaptureZone(zone, region, level);

            Thread.Sleep(1);
        });

        // A restart creates the backend's resources anew, so it's expensive: not too often.
        stress.Run("restart", _ =>
        {
            Thread.Sleep(100);
            screenCapture.Restart();
        });

        stress.Run("register", i =>
        {
            ICaptureZone zone = screenCapture.RegisterCaptureZone(regions[i % regions.Length], levels[i % levels.Length]);
            Thread.Sleep(1);
            screenCapture.UnregisterCaptureZone(zone);
        });

        for (int reader = 0; reader < 2; reader++)
            stress.Run($"frames {reader}", _ =>
            {
                foreach (ICaptureZone zone in zones)
                    AssertFrameSize(zone, sizes);
            });

        stress.Run("properties", i =>
        {
            Assert.AreEqual(display, screenCapture.Display);

            foreach (ICaptureZone zone in zones)
            {
                Assert.Contains(zone.Region, regionSet);
                Assert.AreEqual(display, zone.Display);
                Assert.Contains(zone.DownscaleLevel, levels);
                Assert.IsTrue(zone.IsValid);

                if ((i % 10) == 0)
                    zone.RequestUpdate();
            }
        });

        Thread.Sleep(duration);
        KeyValuePair<CaptureResult, int>[] resultsBeforeDispose = [.. results];

        // On another thread, so a deadlock fails the test instead of blocking it.
        Task dispose = Task.Run(service.Dispose);
        try
        {
            Assert.IsTrue(dispose.Wait(timeout), "Disposing the service didn't finish (deadlock?).");
            Thread.Sleep(200);
        }
        finally
        {
            stress.StopAndAssert();
        }

        Assert.IsTrue(loop.Completion.Wait(timeout), "The capture loop didn't end after the capture was disposed.");
        Assert.IsTrue(loop.Completion.IsCompletedSuccessfully);
        Assert.IsEmpty(errors, $"Errors: {string.Join(" | ", errors)}");

        // Restarts make a capture return Restarting; anything else means it broke.
        Assert.IsTrue(resultsBeforeDispose.All(r => r.Key is CaptureResult.NewFrame or CaptureResult.NoChange or CaptureResult.Timeout or CaptureResult.Restarting),
                      $"Results: {string.Join(", ", resultsBeforeDispose)}");
    }

    private static void AssertFrameSize(ICaptureZone zone, HashSet<(int Width, int Height)> sizes)
    {
        using CaptureFrame frame = zone.AcquireFrame();

        Assert.Contains((frame.Width, frame.Height), sizes, "The frame has a size the zone never had.");
        Assert.AreEqual(frame.Width * frame.ColorFormat.BytesPerPixel, frame.Stride);
        Assert.AreEqual(frame.Stride * frame.Height, frame.RawBuffer.Length);
    }

    /// <summary>
    /// Disposes services while another thread enumerates: the enumeration finishes or throws an <see cref="ObjectDisposedException"/>, but never uses released native objects.
    /// </summary>
    public static void AssertEnumerationDuringDisposeIsSafe(Func<IScreenCaptureService> createService)
    {
        // Enumerations are short, so many rounds with spread dispose times.
        for (int round = 0; round < 200; round++)
        {
            IScreenCaptureService service = createService();

            using Barrier start = new(2);
            Task enumerate = Task.Factory.StartNew(() =>
            {
                start.SignalAndWait();
                try
                {
                    while (true)
                        foreach (GraphicsCard graphicsCard in service.GetGraphicsCards())
                            foreach (Display _ in service.GetDisplays(graphicsCard)) { }
                }
                catch (ObjectDisposedException) { /* Expected once the service is disposed. */ }
            }, TaskCreationOptions.LongRunning);

            // Dispose at different points of an enumeration.
            start.SignalAndWait();
            Thread.SpinWait((round % 20) * 2000);
            service.Dispose();

            Assert.IsTrue(enumerate.Wait(TimeSpan.FromSeconds(30)), "The enumeration didn't finish.");
        }
    }

    /// <summary>
    /// Disposes a capture and its service more than once (the service disposes its captures again) and checks that this doesn't throw.
    /// </summary>
    public static void AssertDisposeIsRepeatable(IScreenCaptureService service)
    {
        (_, Display display) = GetFirstDisplay(service);
        IScreenCapture screenCapture = service.GetScreenCapture(display);
        screenCapture.RegisterCaptureZone(new CaptureRegion(0, 0, display.Width, display.Height));
        CaptureFrame(screenCapture);

        screenCapture.Dispose();
        screenCapture.Dispose();
        Assert.ThrowsExactly<ObjectDisposedException>(() => screenCapture.CaptureScreen());

        service.Dispose();
        service.Dispose();
        Assert.ThrowsExactly<ObjectDisposedException>(() => service.GetGraphicsCards().ToList());
    }

    /// <summary>
    /// Checks that every graphics card and display has a unique id and that enumerating again gives the same values.
    /// </summary>
    public static void AssertDevicesHaveStableIds(IScreenCaptureService service)
    {
        List<GraphicsCard> graphicsCards = [.. service.GetGraphicsCards()];
        List<Display> displays = [.. GetAllDisplays(service)];
        Assert.IsNotEmpty(displays, "No display found.");

        AssertUniqueIds(graphicsCards.Select(c => c.Id));
        AssertUniqueIds(displays.Select(d => d.Id));

        foreach (GraphicsCard graphicsCard in graphicsCards)
            foreach (Display display in service.GetDisplays(graphicsCard))
                Assert.AreEqual(graphicsCard, display.GraphicsCard, $"{display.Id}: graphics card");

        // Enumerating again gives the same values.
        CollectionAssert.AreEqual(graphicsCards, service.GetGraphicsCards().ToList());
        CollectionAssert.AreEqual(displays, GetAllDisplays(service).ToList());
    }

    private static void AssertUniqueIds(IEnumerable<string> ids)
    {
        List<string> list = [.. ids];
        Assert.IsTrue(list.TrueForAll(id => !string.IsNullOrEmpty(id)), "Empty id.");
        Assert.HasCount(list.Count, list.Distinct(StringComparer.Ordinal), $"Duplicate ids: {string.Join(", ", list)}");
    }

    /// <summary>
    /// Checks that <see cref="IScreenCaptureService.GetDisplays"/> finds the graphics card by its id, not by its index.
    /// </summary>
    public static void AssertDisplaysAreFoundById(IScreenCaptureService service)
    {
        (GraphicsCard graphicsCard, _) = GetFirstDisplay(service);
        List<Display> displays = [.. service.GetDisplays(graphicsCard)];

        // The displays carry the card's current index, which the capture needs, not the one passed in.
        CollectionAssert.AreEqual(displays, service.GetDisplays(graphicsCard with { Index = 99 }).ToList());

        Assert.IsEmpty(service.GetDisplays(graphicsCard with { Id = "unknown" }));
    }

    /// <summary>
    /// Checks that every enumerated graphics card and display is found again by its id alone, like an id stored in settings.
    /// </summary>
    public static void AssertDevicesAreLookedUpById(IScreenCaptureService service)
    {
        List<GraphicsCard> graphicsCards = [.. service.GetGraphicsCards()];
        List<Display> displays = [.. GetAllDisplays(service)];
        Assert.IsNotEmpty(displays, "No display found.");

        foreach (GraphicsCard graphicsCard in graphicsCards)
        {
            Assert.IsTrue(service.TryGetGraphicsCard(graphicsCard.Id, out GraphicsCard found), $"{graphicsCard.Id}: not found");
            Assert.AreEqual(graphicsCard, found);
        }

        foreach (Display display in displays)
        {
            Assert.IsTrue(service.TryGetDisplay(display.Id, out Display found), $"{display.Id}: not found");
            Assert.AreEqual(display, found);
        }

        Assert.IsFalse(service.TryGetGraphicsCard("unknown", out _));
        Assert.IsFalse(service.TryGetDisplay("unknown", out _));
    }

    /// <summary>
    /// Checks that the service returns the same capture for displays with the same id, also if their other values changed (e.g. the resolution).
    /// </summary>
    public static void AssertCapturesAreCachedById(IScreenCaptureService service)
    {
        (_, Display display) = GetFirstDisplay(service);

        IScreenCapture screenCapture = service.GetScreenCapture(display);
        Assert.AreSame(screenCapture, service.GetScreenCapture(display with { Width = display.Width / 2, Height = display.Height / 2 }));
        Assert.AreSame(screenCapture, service.GetScreenCapture(display with { Index = 99 }));
        Assert.AreEqual(display, screenCapture.Display);

        Assert.AreNotSame(screenCapture, service.GetScreenCapture(display with { Id = "other" }));
    }

    /// <summary>
    /// Checks that a capture disposed by its user is replaced by a new one that captures, and that disposing the service disposes that one too.
    /// </summary>
    public static void AssertDisposedCaptureIsReplaced(IScreenCaptureService service)
    {
        (_, Display display) = GetFirstDisplay(service);
        IScreenCapture screenCapture = service.GetScreenCapture(display);
        screenCapture.Dispose();

        IScreenCapture replacement = service.GetScreenCapture(display);
        Assert.AreNotSame(screenCapture, replacement);
        Assert.AreSame(replacement, service.GetScreenCapture(display));

        // The old capture released the display, so the new one can capture it.
        replacement.RegisterCaptureZone(new CaptureRegion(0, 0, 64, 64));
        CaptureFrame(replacement);

        service.Dispose();
        Assert.ThrowsExactly<ObjectDisposedException>(() => replacement.CaptureScreen());
        Assert.ThrowsExactly<ObjectDisposedException>(() => service.GetScreenCapture(display));
    }

    /// <summary>
    /// Checks that concurrent requests for the same display get the same capture.
    /// </summary>
    public static void AssertConcurrentRequestsGetTheSameCapture(IScreenCaptureService service)
    {
        const int threads = 8;

        (_, Display display) = GetFirstDisplay(service);

        using Barrier start = new(threads);
        Task<IScreenCapture>[] requests = [.. Enumerable.Range(0, threads).Select(_ => Task.Factory.StartNew(() =>
        {
            start.SignalAndWait();
            return service.GetScreenCapture(display);
        }, TaskCreationOptions.LongRunning))];

        Assert.IsTrue(Task.WaitAll(requests, TimeSpan.FromSeconds(30)), "The requests didn't finish.");
        Assert.HasCount(1, requests.Select(r => r.Result).Distinct());
    }

    public static (int x, int y, int width, int height) FullScreen(Display display) => (0, 0, display.Width, display.Height);

    public static (int x, int y, int width, int height) Center(Display display) => (display.Width / 4, display.Height / 4, display.Width / 2, display.Height / 2);
}
