extern alias WGC;
using System.Diagnostics;
using System.Runtime.Versioning;
using HPPH;
using WGC::ScreenCapture.NET;

namespace ScreenCapture.NET.Tests.Hardware;

/// <summary>
/// Window captures of the Windows.Graphics.Capture backend, on test pattern windows. They show small windows in the center of a display for a moment.
/// </summary>
[TestClass]
[TestCategory(HardwareTestHelper.CATEGORY)]
[OSCondition(OperatingSystems.Windows)]
[SupportedOSPlatform("windows")]
public sealed class WGCWindowHardwareTests
{
    #region Constants

    private const int WIDTH = 256;
    private const int HEIGHT = 16;
    private const string TITLE = "ScreenCapture.NET window capture test";

    #endregion

    #region Methods

    // The pattern window is checked pixel by pixel: on a display showing HDR Windows converts it.
    private static Display GetSdrDisplay(WGCScreenCaptureService service)
    {
        foreach (Display display in HardwareTestHelper.GetAllDisplays(service))
            if (!DX11HardwareTests.IsShowingHdr(display))
                return display;

        Assert.Inconclusive("Every display shows HDR.");
        throw new InvalidOperationException();
    }

    private static (int X, int Y) GetCenter(Display display, int width, int height) => (display.X + ((display.Width - width) / 2), display.Y + ((display.Height - height) / 2));

    private static TestPatternWindow ShowPattern(Display display, ColorBGRA[] pattern, bool framed = false)
    {
        (int x, int y) = GetCenter(display, WIDTH, HEIGHT);
        return new TestPatternWindow(x, y, WIDTH, HEIGHT, pattern, TITLE, framed, capturable: true);
    }

    private static ColorBGRA[] Invert(ColorBGRA[] pattern) => [.. pattern.Select(c => new ColorBGRA((byte)(255 - c.B), (byte)(255 - c.G), (byte)(255 - c.R), 255))];

    // Captures until the zone shows the expected pixels, at most for 3 s; returns the largest difference of the last capture.
    private static int CaptureUntil(WGCScreenCapture screenCapture, CaptureZone<ColorBGRA> zone, ColorBGRA[] expected)
    {
        int maxDifference;
        Stopwatch stopwatch = Stopwatch.StartNew();
        do
        {
            screenCapture.CaptureScreen();
            maxDifference = DX11HardwareTests.Compare(expected, HardwareTestHelper.GetPixels(zone)).MaxDifference;
        } while ((maxDifference > 0) && (stopwatch.Elapsed < TimeSpan.FromSeconds(3)));

        return maxDifference;
    }

    // The top left part of an image.
    private static ColorBGRA[] Crop(ColorBGRA[] image, int imageWidth, int width, int height)
        => [.. Enumerable.Range(0, width * height).Select(i => image[((i / width) * imageWidth) + (i % width)])];

    [TestMethod]
    public void CapturesAWindow()
    {
        using WGCScreenCaptureService service = new(new WGCCaptureOptions { Timeout = TimeSpan.FromMilliseconds(100) });
        Display display = GetSdrDisplay(service);
        ColorBGRA[] pattern = DX11HardwareTests.CreateSdrPattern(WIDTH, HEIGHT);
        using TestPatternWindow window = ShowPattern(display, pattern);

        WGCScreenCapture screenCapture = service.GetWindowCapture(window.Handle);
        Assert.IsNull(screenCapture.LastError, $"{screenCapture.LastError}");
        Assert.AreEqual(window.Handle, screenCapture.Window);

        (int x, int y) = GetCenter(display, WIDTH, HEIGHT);
        Display expected = new($"window:0x{(long)window.Handle:X}", -1, display.DeviceName, TITLE, x, y, WIDTH, HEIGHT, ImageRotation.None, display.GraphicsCard);
        Assert.AreEqual(expected, screenCapture.Display);

        List<ScreenCaptureDisplayChangedEventArgs> changes = [];
        screenCapture.DisplayChanged += (_, e) => changes.Add(e);

        CaptureZone<ColorBGRA> zone = screenCapture.RegisterCaptureZone(new CaptureRegion(0, 0, WIDTH, HEIGHT));
        Assert.AreEqual(0, CaptureUntil(screenCapture, zone, pattern), "The capture doesn't show the window.");
        Assert.IsEmpty(changes, "The display of the window changed with the first capture.");
    }

    /// <summary>
    /// A window with a frame (title bar, borders) is captured with it: the display of the capture has the size of the frames from the start (the bounds of the window without the shadow).
    /// </summary>
    [TestMethod]
    public void FramedWindowHasTheSizeOfItsFrames()
    {
        using WGCScreenCaptureService service = new(new WGCCaptureOptions { Timeout = TimeSpan.FromMilliseconds(100) });
        Display display = GetSdrDisplay(service);
        using TestPatternWindow window = ShowPattern(display, DX11HardwareTests.CreateSdrPattern(WIDTH, HEIGHT), framed: true);

        WGCScreenCapture screenCapture = service.GetWindowCapture(window.Handle);
        Display before = screenCapture.Display;
        Console.WriteLine($"Framed window: {before.Width}x{before.Height} at {before.X}, {before.Y}.");

        List<ScreenCaptureDisplayChangedEventArgs> changes = [];
        screenCapture.DisplayChanged += (_, e) => changes.Add(e);
        HardwareTestHelper.CaptureFrame(screenCapture);

        Assert.IsEmpty(changes, $"The display of the window changed with the first capture: {string.Join(", ", changes.Select(c => $"{c.PreviousDisplay} -> {c.Display}"))}.");
        Assert.AreEqual(before, screenCapture.Display);
    }

    [TestMethod]
    public void CapturesACoveredWindow()
    {
        using WGCScreenCaptureService service = new(new WGCCaptureOptions { Timeout = TimeSpan.FromMilliseconds(100) });
        Display display = GetSdrDisplay(service);
        ColorBGRA[] pattern = DX11HardwareTests.CreateSdrPattern(WIDTH, HEIGHT);
        using TestPatternWindow window = ShowPattern(display, pattern);

        WGCScreenCapture screenCapture = service.GetWindowCapture(window.Handle);
        CaptureZone<ColorBGRA> zone = screenCapture.RegisterCaptureZone(new CaptureRegion(0, 0, WIDTH, HEIGHT));
        Assert.AreEqual(0, CaptureUntil(screenCapture, zone, pattern), "The capture doesn't show the window.");

        // Shown later, so it's on top of the first one.
        using TestPatternWindow cover = ShowPattern(display, Invert(pattern));
        Thread.Sleep(200);
        for (int i = 0; i < 5; i++)
            screenCapture.CaptureScreen();

        (int maxDifference, int differentPixels) = DX11HardwareTests.Compare(pattern, HardwareTestHelper.GetPixels(zone));
        Assert.AreEqual(0, maxDifference, $"The covered window isn't captured: {differentPixels} pixels differ, by up to {maxDifference}.");
    }

    /// <summary>
    /// A resized window restarts the capture: the display of the capture gets the new size, zones that don't fit anymore become invalid, the others show the window as it's now.
    /// Grown beyond its first size, the zones fit again and get new frames (which need buffers of the new size).
    /// </summary>
    [TestMethod]
    public void ResizedWindowRestartsWithItsNewSize()
    {
        const int smallWidth = WIDTH / 2;
        const int smallHeight = HEIGHT / 2;

        using WGCScreenCaptureService service = new(new WGCCaptureOptions { Timeout = TimeSpan.FromMilliseconds(100) });
        Display display = GetSdrDisplay(service);
        ColorBGRA[] pattern = DX11HardwareTests.CreateSdrPattern(WIDTH, HEIGHT);
        using TestPatternWindow window = ShowPattern(display, pattern);

        WGCScreenCapture screenCapture = service.GetWindowCapture(window.Handle);
        CaptureZone<ColorBGRA> full = screenCapture.RegisterCaptureZone(new CaptureRegion(0, 0, WIDTH, HEIGHT));
        CaptureZone<ColorBGRA> small = screenCapture.RegisterCaptureZone(new CaptureRegion(0, 0, smallWidth, smallHeight));
        Assert.AreEqual(0, CaptureUntil(screenCapture, full, pattern), "The capture doesn't show the window.");

        List<ScreenCaptureDisplayChangedEventArgs> changes = [];
        screenCapture.DisplayChanged += (_, e) => changes.Add(e);
        Display before = screenCapture.Display;

        window.Resize(smallWidth, smallHeight);

        List<CaptureResult> results = [];
        Stopwatch stopwatch = Stopwatch.StartNew();
        while ((changes.Count == 0) && (stopwatch.Elapsed < TimeSpan.FromSeconds(3)))
            results.Add(screenCapture.CaptureScreen());

        Assert.HasCount(1, changes, $"Results: {string.Join(", ", results)}.");
        Assert.AreEqual(before with { Width = smallWidth, Height = smallHeight }, changes[0].Display);
        Assert.AreEqual(changes[0].Display, screenCapture.Display);
        Assert.Contains(CaptureResult.Restarting, results);
        Assert.IsNull(screenCapture.LastError, $"{screenCapture.LastError}");

        Assert.IsFalse(full.IsValid, "The zone larger than the window is still valid.");
        Assert.IsTrue(small.IsValid);
        Assert.AreEqual(0, CaptureUntil(screenCapture, small, Crop(pattern, WIDTH, smallWidth, smallHeight)), "The capture doesn't show the resized window.");

        window.Resize(WIDTH, HEIGHT * 2);
        results.Clear();
        stopwatch.Restart();
        while (!((changes.Count == 2) && results.Contains(CaptureResult.NewFrame)) && (stopwatch.Elapsed < TimeSpan.FromSeconds(3)))
            if ((screenCapture.CaptureScreen() is var result) && (changes.Count == 2))
                results.Add(result);

        Assert.HasCount(2, changes);
        Assert.AreEqual(before with { Height = HEIGHT * 2 }, screenCapture.Display);
        Assert.Contains(CaptureResult.NewFrame, results, $"No new frame after the window grew: {string.Join(", ", results)}.");
        Assert.IsTrue(full.IsValid, "The zone that fits again isn't valid.");
        Assert.AreEqual(0, CaptureUntil(screenCapture, full, pattern), "The capture doesn't show the grown window.");
    }

    /// <summary>
    /// A window moved to another display restarts the capture with the display it's on now. Moving within a display changes nothing.
    /// </summary>
    [TestMethod]
    public void WindowMovedToAnotherDisplayIsReadAgain()
    {
        using WGCScreenCaptureService service = new(new WGCCaptureOptions { Timeout = TimeSpan.FromMilliseconds(100) });
        List<Display> sdrDisplays = [.. HardwareTestHelper.GetAllDisplays(service).Where(d => !DX11HardwareTests.IsShowingHdr(d))];
        if (sdrDisplays.Count < 2)
        {
            Assert.Inconclusive("Needs two displays that don't show HDR.");
            return;
        }

        ColorBGRA[] pattern = DX11HardwareTests.CreateSdrPattern(WIDTH, HEIGHT);
        using TestPatternWindow window = ShowPattern(sdrDisplays[0], pattern);

        WGCScreenCapture screenCapture = service.GetWindowCapture(window.Handle);
        CaptureZone<ColorBGRA> zone = screenCapture.RegisterCaptureZone(new CaptureRegion(0, 0, WIDTH, HEIGHT));
        Assert.AreEqual(0, CaptureUntil(screenCapture, zone, pattern), "The capture doesn't show the window.");

        List<ScreenCaptureDisplayChangedEventArgs> changes = [];
        screenCapture.DisplayChanged += (_, e) => changes.Add(e);

        // Within the display: no restart, the old position is kept.
        (int x, int y) = GetCenter(sdrDisplays[0], WIDTH, HEIGHT);
        window.Move(x + 10, y + 10);
        for (int i = 0; i < 5; i++)
            Assert.AreNotEqual(CaptureResult.Restarting, screenCapture.CaptureScreen());
        Assert.IsEmpty(changes, "Moving the window within its display changed the display of the capture.");

        Display other = sdrDisplays[1];
        (x, y) = GetCenter(other, WIDTH, HEIGHT);
        window.Move(x, y);

        List<CaptureResult> results = [];
        Stopwatch stopwatch = Stopwatch.StartNew();
        while ((changes.Count == 0) && (stopwatch.Elapsed < TimeSpan.FromSeconds(3)))
            results.Add(screenCapture.CaptureScreen());

        Assert.HasCount(1, changes, $"Results: {string.Join(", ", results)}.");
        Display moved = screenCapture.Display;
        Assert.AreEqual((other.DeviceName, other.GraphicsCard, x, y, WIDTH, HEIGHT), (moved.DeviceName, moved.GraphicsCard, moved.X, moved.Y, moved.Width, moved.Height));
        Assert.AreEqual(0, CaptureUntil(screenCapture, zone, pattern), "The capture doesn't show the moved window.");
    }

    /// <summary>
    /// A minimized window has no new frames and keeps its size; restored, it's captured again.
    /// </summary>
    [TestMethod]
    public void MinimizedWindowHasNoNewFrames()
    {
        using WGCScreenCaptureService service = new(new WGCCaptureOptions { Timeout = TimeSpan.FromMilliseconds(100) });
        Display display = GetSdrDisplay(service);
        ColorBGRA[] pattern = DX11HardwareTests.CreateSdrPattern(WIDTH, HEIGHT);
        using TestPatternWindow window = ShowPattern(display, pattern);

        WGCScreenCapture screenCapture = service.GetWindowCapture(window.Handle);
        CaptureZone<ColorBGRA> zone = screenCapture.RegisterCaptureZone(new CaptureRegion(0, 0, WIDTH, HEIGHT));
        Assert.AreEqual(0, CaptureUntil(screenCapture, zone, pattern), "The capture doesn't show the window.");

        List<ScreenCaptureDisplayChangedEventArgs> changes = [];
        screenCapture.DisplayChanged += (_, e) => changes.Add(e);

        window.Minimize();
        List<CaptureResult> results = [];
        for (int i = 0; i < 10; i++)
            results.Add(screenCapture.CaptureScreen());
        Console.WriteLine($"Minimized: {string.Join(", ", results)}; {screenCapture.Display}; {screenCapture.LastError?.Message}");

        // The zone keeps the last frame. Windows can deliver a frame of the minimized title bar: it's no new frame (NoChange) and no new size.
        CollectionAssert.AreEqual(pattern, HardwareTestHelper.GetPixels(zone), "The zone changed while the window was minimized.");
        Assert.IsTrue(zone.IsValid, "The zone became invalid while the window was minimized.");
        Assert.IsTrue(results.Skip(2).All(r => r is CaptureResult.Timeout or CaptureResult.NoChange), $"Results: {string.Join(", ", results)}.");

        window.Restore();
        Assert.AreEqual(0, CaptureUntil(screenCapture, zone, pattern), "The capture doesn't show the restored window.");
        Assert.IsEmpty(changes, $"Minimizing the window changed the display of the capture: {string.Join(", ", changes.Select(c => $"{c.PreviousDisplay} -> {c.Display}"))}.");
    }

    [TestMethod]
    public void ClosedWindowIsUnavailable()
    {
        using WGCScreenCaptureService service = new(new WGCCaptureOptions { Timeout = TimeSpan.FromMilliseconds(100) });
        Display display = GetSdrDisplay(service);
        WGCScreenCapture screenCapture;
        using (TestPatternWindow window = ShowPattern(display, DX11HardwareTests.CreateSdrPattern(WIDTH, HEIGHT)))
        {
            screenCapture = service.GetWindowCapture(window.Handle);
            HardwareTestHelper.CaptureFrame(screenCapture);
        }

        List<CaptureResult> results = [];
        Stopwatch stopwatch = Stopwatch.StartNew();
        while ((results.LastOrDefault() != CaptureResult.Unavailable) && (stopwatch.Elapsed < TimeSpan.FromSeconds(3)))
            results.Add(screenCapture.CaptureScreen());

        Assert.AreEqual(CaptureResult.Unavailable, results[^1], $"Results: {string.Join(", ", results)}.");
        InvalidOperationException error = Assert.IsInstanceOfType<InvalidOperationException>(screenCapture.LastError);
        StringAssert.Contains(error.Message, "doesn't exist anymore");

        // Restarts keep failing; nothing is left behind.
        Assert.AreEqual(CaptureResult.Unavailable, screenCapture.CaptureScreen());
        WGCHardwareTests.AssertNoObjects(screenCapture);
    }

    /// <summary>
    /// Windows.Graphics.Capture refuses some windows (e.g. tool windows, like the taskbar): the capture tells why and is unavailable.
    /// </summary>
    [TestMethod]
    public void RefusedWindowIsReported()
    {
        using WGCScreenCaptureService service = new(new WGCCaptureOptions { Timeout = TimeSpan.FromMilliseconds(100) });
        Display display = GetSdrDisplay(service);
        (int x, int y) = GetCenter(display, WIDTH, HEIGHT);
        using TestPatternWindow window = new(x, y, WIDTH, HEIGHT, DX11HardwareTests.CreateSdrPattern(WIDTH, HEIGHT), TITLE);

        WGCScreenCapture screenCapture = service.GetWindowCapture(window.Handle);

        InvalidOperationException error = Assert.IsInstanceOfType<InvalidOperationException>(screenCapture.LastError);
        StringAssert.Contains(error.Message, "can't capture");
        Assert.IsInstanceOfType<ArgumentException>(error.InnerException);
        Assert.AreEqual(CaptureResult.Unavailable, screenCapture.CaptureScreen());
        WGCHardwareTests.AssertNoObjects(screenCapture);
    }

    [TestMethod]
    public void InvalidWindowsThrow()
    {
        using WGCScreenCaptureService service = new();

        Assert.ThrowsExactly<ArgumentException>(() => service.GetWindowCapture(0));
        Assert.ThrowsExactly<ArgumentException>(() => service.GetWindowCapture(0x7FFF_FFF0));

        nint closed;
        using (TestPatternWindow window = new(0, 0, 1, 1, new ColorBGRA[1]))
            closed = window.Handle;
        Assert.ThrowsExactly<ArgumentException>(() => service.GetWindowCapture(closed));
    }

    [TestMethod]
    public void WindowCapturesAreCached()
    {
        WGCCaptureOptions options = new() { Timeout = TimeSpan.FromMilliseconds(100) };
        using WGCScreenCaptureService service = new(options);
        Display display = GetSdrDisplay(service);
        using TestPatternWindow window = ShowPattern(display, DX11HardwareTests.CreateSdrPattern(WIDTH, HEIGHT));

        WGCScreenCapture screenCapture = service.GetWindowCapture(window.Handle);
        Assert.AreSame(options, screenCapture.Options);
        Assert.AreSame(screenCapture, service.GetWindowCapture(window.Handle));
        Assert.AreSame(screenCapture, service.GetWindowCapture(window.Handle, options with { }));
        Assert.ThrowsExactly<InvalidOperationException>(() => service.GetWindowCapture(window.Handle, WGCCaptureOptions.Default));
        Assert.ThrowsExactly<ArgumentNullException>(() => service.GetWindowCapture(window.Handle, null!));

        // Its own capture, not the one of the display it's on.
        Assert.AreNotSame(screenCapture, service.GetScreenCapture(display));

        screenCapture.Dispose();
        WGCScreenCapture replaced = service.GetWindowCapture(window.Handle);
        Assert.AreNotSame(screenCapture, replaced);

        service.Dispose();
        Assert.IsTrue(replaced.IsDisposed);
        WGCHardwareTests.AssertNoObjects(replaced);
    }

    #endregion
}
