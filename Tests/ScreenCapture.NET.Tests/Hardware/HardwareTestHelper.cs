using System.Runtime.InteropServices;
using HPPH;

namespace ScreenCapture.NET.Tests.Hardware;

/// <summary>
/// Shared checks for backend smoke tests. These need a real GPU/display and are excluded from CI via TestCategory "Hardware".
/// </summary>
internal static class HardwareTestHelper
{
    public const string CATEGORY = "Hardware";

    private const int CAPTURE_ATTEMPTS = 20;

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
        ICaptureZone zone = screenCapture.RegisterCaptureZone(x, y, width, height, downscaleLevel);

        int updates = 0;
        zone.Updated += (_, _) => updates++;

        // The first frame after (re)starting a capture can take a few attempts, e.g. if the desktop doesn't change.
        bool captured = false;
        for (int i = 0; (i < CAPTURE_ATTEMPTS) && !captured; i++)
            captured = screenCapture.CaptureScreen();

        Assert.IsTrue(captured, $"No frame captured after {CAPTURE_ATTEMPTS} attempts.");
        Assert.IsGreaterThan(0, updates, "Zone wasn't updated.");

        Assert.AreEqual(width >> zone.DownscaleLevel, zone.Width);
        Assert.AreEqual(height >> zone.DownscaleLevel, zone.Height);

        using (zone.Lock())
        {
            Assert.AreEqual(zone.Stride * zone.Height, zone.RawBuffer.Length);
            Assert.IsTrue(zone.RawBuffer.ContainsAnyExcept((byte)0), "Captured zone contains only zeros.");
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

        ICaptureZone moved = screenCapture.RegisterCaptureZone(0, 0, size, size, downscaleLevel);
        CaptureFrame(screenCapture);

        screenCapture.UpdateCaptureZone(moved, x: centerX, y: centerY);
        ICaptureZone reference = screenCapture.RegisterCaptureZone(centerX, centerY, size, size, downscaleLevel);
        ICaptureZone topLeft = screenCapture.RegisterCaptureZone(0, 0, size, size, downscaleLevel);

        // No new frame needed: the zones are updated from the last captured frame even if this returns false (e.g. on a static desktop).
        screenCapture.CaptureScreen();

        byte[] movedPixels;
        byte[] referencePixels;
        byte[] topLeftPixels;
        using (moved.Lock()) movedPixels = moved.RawBuffer.ToArray();
        using (reference.Lock()) referencePixels = reference.RawBuffer.ToArray();
        using (topLeft.Lock()) topLeftPixels = topLeft.RawBuffer.ToArray();

        screenCapture.UnregisterCaptureZone(moved);
        screenCapture.UnregisterCaptureZone(reference);
        screenCapture.UnregisterCaptureZone(topLeft);

        if (topLeftPixels.AsSpan().SequenceEqual(referencePixels))
            Assert.Inconclusive("The top-left corner and the center of the screen look the same, so a move can't be detected.");

        Assert.IsTrue(movedPixels.AsSpan().SequenceEqual(referencePixels), "The moved zone doesn't show the pixels at its new position.");
    }

    /// <summary>
    /// Changes, registers and unregisters zones while holding a zone lock, while another thread captures continuously.
    /// This used to deadlock: the capture thread held its locks and waited for the zone lock, the user thread held the zone lock and waited for them.
    /// </summary>
    public static void AssertZonesCanBeChangedWhileCapturing(IScreenCaptureService service, int downscaleLevel = 0)
    {
        const int iterations = 200;
        TimeSpan timeout = TimeSpan.FromSeconds(30);

        (_, Display display) = GetFirstDisplay(service);
        IScreenCapture screenCapture = service.GetScreenCapture(display);
        ICaptureZone zone = screenCapture.RegisterCaptureZone(0, 0, 64, 64, downscaleLevel);
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
                using (zone.Lock())
                    screenCapture.UpdateCaptureZone(zone, x: i % 100, width: 64 + (i % 2));

                using (zone.Lock())
                    screenCapture.UnregisterCaptureZone(screenCapture.RegisterCaptureZone(0, 0, 32, 32, downscaleLevel));
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
    /// Checks on every display that partial zones show the same pixels as the same area of a full-screen zone.
    /// On rotated displays this checks that the source region of a partial zone is mirrored like the full-screen copy.
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

            ICaptureZone fullScreen = screenCapture.RegisterCaptureZone(0, 0, width, height);
            ICaptureZone[] zones = [.. regions.Select(r => screenCapture.RegisterCaptureZone(r.x, r.y, r.width, r.height))];

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
    /// <paramref name="reference"/> has to capture in display orientation (like DX9), so this checks the rotation of <paramref name="service"/>.
    /// </summary>
    /// <remarks>
    /// The two captures are taken one after the other, so animated content can differ. Instead of comparing pixels this compares the average
    /// brightness of a coarse grid, and requires the image to be much closer to the reference than the same image rotated by 180°
    /// (which is what mixing up 90° and 270° would produce).
    /// </remarks>
    public static void AssertMatchesReferenceOrientation(IScreenCaptureService service, IScreenCaptureService reference)
    {
        // Below this (average brightness difference, 0-255) the screen looks too much like itself rotated by 180° to tell the difference.
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

    private static IEnumerable<Display> GetAllDisplays(IScreenCaptureService service)
        => service.GetGraphicsCards().SelectMany(service.GetDisplays);

    private static ColorBGRA[] CaptureFullScreen(IScreenCaptureService service, Display display)
    {
        IScreenCapture screenCapture = service.GetScreenCapture(display);
        ICaptureZone zone = screenCapture.RegisterCaptureZone(0, 0, display.Width, display.Height);

        CaptureFrame(screenCapture);
        ColorBGRA[] pixels = GetPixels(zone);

        screenCapture.UnregisterCaptureZone(zone);
        return pixels;
    }

    private static ColorBGRA[] GetPixels(ICaptureZone zone)
    {
        using (zone.Lock())
        {
            Assert.AreEqual(ColorBGRA.ColorFormat, zone.ColorFormat, "Only BGRA zones are supported.");
            return [.. MemoryMarshal.Cast<byte, ColorBGRA>(zone.RawBuffer)];
        }
    }

    private static ColorBGRA[] Crop(ColorBGRA[] pixels, int stride, int x, int y, int width, int height)
    {
        ColorBGRA[] result = new ColorBGRA[width * height];
        for (int row = 0; row < height; row++)
            pixels.AsSpan(((y + row) * stride) + x, width).CopyTo(result.AsSpan(row * width));

        return result;
    }

    private static void CaptureFrame(IScreenCapture screenCapture)
    {
        bool captured = false;
        for (int i = 0; (i < CAPTURE_ATTEMPTS) && !captured; i++)
            captured = screenCapture.CaptureScreen();

        Assert.IsTrue(captured, $"No frame captured after {CAPTURE_ATTEMPTS} attempts.");
    }

    /// <summary>
    /// Disposes a capture and its service more than once (the service disposes its captures again) and checks that this doesn't throw.
    /// </summary>
    public static void AssertDisposeIsRepeatable(IScreenCaptureService service)
    {
        (_, Display display) = GetFirstDisplay(service);
        IScreenCapture screenCapture = service.GetScreenCapture(display);
        screenCapture.RegisterCaptureZone(0, 0, display.Width, display.Height);
        CaptureFrame(screenCapture);

        screenCapture.Dispose();
        screenCapture.Dispose();
        Assert.ThrowsExactly<ObjectDisposedException>(() => screenCapture.CaptureScreen());

        service.Dispose();
        service.Dispose();
        Assert.ThrowsExactly<ObjectDisposedException>(() => service.GetGraphicsCards().ToList());
    }

    public static (int x, int y, int width, int height) FullScreen(Display display) => (0, 0, display.Width, display.Height);

    public static (int x, int y, int width, int height) Center(Display display) => (display.Width / 4, display.Height / 4, display.Width / 2, display.Height / 2);
}
