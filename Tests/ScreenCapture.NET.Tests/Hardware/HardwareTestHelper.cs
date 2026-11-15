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

    private static void CaptureFrame(IScreenCapture screenCapture)
    {
        bool captured = false;
        for (int i = 0; (i < CAPTURE_ATTEMPTS) && !captured; i++)
            captured = screenCapture.CaptureScreen();

        Assert.IsTrue(captured, $"No frame captured after {CAPTURE_ATTEMPTS} attempts.");
    }

    public static (int x, int y, int width, int height) FullScreen(Display display) => (0, 0, display.Width, display.Height);

    public static (int x, int y, int width, int height) Center(Display display) => (display.Width / 4, display.Height / 4, display.Width / 2, display.Height / 2);
}
