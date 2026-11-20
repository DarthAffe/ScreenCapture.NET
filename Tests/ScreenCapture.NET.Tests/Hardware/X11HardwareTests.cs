using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using HPPH;

namespace ScreenCapture.NET.Tests.Hardware;

[TestClass]
[TestCategory(HardwareTestHelper.CATEGORY)]
[OSCondition(OperatingSystems.Linux)]
[SupportedOSPlatform("linux")]
public sealed class X11HardwareTests
{
    [TestInitialize]
    public void RequireXServer()
    {
        // Without an X server the service throws (see X11NoServerTests).
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")))
            Assert.Inconclusive("No X server available ($DISPLAY is not set).");
    }

    [TestMethod]
    public void EnumeratesDisplays()
    {
        using X11ScreenCaptureService service = new();
        HardwareTestHelper.AssertEnumeratesDisplays(service);
    }

    [TestMethod]
    public void DevicesHaveStableIds()
    {
        using X11ScreenCaptureService service = new();
        HardwareTestHelper.AssertDevicesHaveStableIds(service);
    }

    [TestMethod]
    public void OptionsAreUsedForCaptures()
    {
        X11CaptureOptions options = new();
        using X11ScreenCaptureService service = new(options);
        Assert.AreSame(options, service.Options);
        Assert.IsNull(service.DisplayName);

        (_, Display display) = HardwareTestHelper.GetFirstDisplay(service);
        X11ScreenCapture screenCapture = service.GetScreenCapture(display);
        Assert.AreSame(options, screenCapture.Options);

        // Equal options return the existing capture.
        Assert.AreSame(screenCapture, service.GetScreenCapture(display, new X11CaptureOptions()));
        Assert.ThrowsExactly<ArgumentNullException>(() => service.GetScreenCapture(display, null!));

        using X11ScreenCaptureService defaultService = new();
        Assert.AreSame(X11CaptureOptions.Default, defaultService.Options);
    }

    [TestMethod]
    public void ServiceConnectsToTheGivenDisplay()
    {
        string displayName = Environment.GetEnvironmentVariable("DISPLAY")!;

        using X11ScreenCaptureService defaultService = new();
        using X11ScreenCaptureService service = new(displayName: displayName);
        Assert.AreEqual(displayName, service.DisplayName);

        CollectionAssert.AreEqual(defaultService.GetGraphicsCards().ToList(), service.GetGraphicsCards().ToList());
        CollectionAssert.AreEqual(HardwareTestHelper.GetAllDisplays(defaultService).ToList(), HardwareTestHelper.GetAllDisplays(service).ToList());

        HardwareTestHelper.AssertCapturesZone(service, HardwareTestHelper.Center);
    }

    [TestMethod]
    public void UnreachableDisplayIsReported()
    {
        const string displayName = ":4711";

        Display display;
        using (X11ScreenCaptureService defaultService = new())
            display = HardwareTestHelper.GetFirstDisplay(defaultService).display;

        using X11ScreenCaptureService service = new(displayName: displayName);

        InvalidOperationException exception = Assert.ThrowsExactly<InvalidOperationException>(() => service.GetGraphicsCards());
        StringAssert.Contains(exception.Message, displayName);

        // The captures connect to the display of the service.
        X11ScreenCapture screenCapture = service.GetScreenCapture(display);
        Assert.AreEqual(CaptureResult.Unavailable, screenCapture.CaptureScreen());

        InvalidOperationException error = Assert.IsInstanceOfType<InvalidOperationException>(screenCapture.LastError);
        StringAssert.Contains(error.Message, displayName);
    }

    [TestMethod]
    public void DisplaysAreFoundById()
    {
        using X11ScreenCaptureService service = new();
        HardwareTestHelper.AssertDisplaysAreFoundById(service);
    }

    [TestMethod]
    public void DevicesAreLookedUpById()
    {
        using X11ScreenCaptureService service = new();
        HardwareTestHelper.AssertDevicesAreLookedUpById(service);
    }

    [TestMethod]
    public void DisposedCaptureIsReplaced()
    {
        using X11ScreenCaptureService service = new();
        HardwareTestHelper.AssertDisposedCaptureIsReplaced(service);
    }

    [TestMethod]
    public void ConcurrentRequestsGetTheSameCapture()
    {
        using X11ScreenCaptureService service = new();
        HardwareTestHelper.AssertConcurrentRequestsGetTheSameCapture(service);
    }

    [TestMethod]
    public void CapturesAreCachedById()
    {
        using X11ScreenCaptureService service = new();
        HardwareTestHelper.AssertCapturesAreCachedById(service);
    }

    [TestMethod]
    public void CapturesFullScreenZone()
    {
        using X11ScreenCaptureService service = new();
        HardwareTestHelper.AssertCapturesZone(service, HardwareTestHelper.FullScreen);
    }

    [TestMethod]
    public void CapturesPartialZone()
    {
        using X11ScreenCaptureService service = new();
        HardwareTestHelper.AssertCapturesZone(service, HardwareTestHelper.Center);
    }

    [TestMethod]
    public void CapturesDownscaledZone()
    {
        using X11ScreenCaptureService service = new();
        HardwareTestHelper.AssertCapturesZone(service, HardwareTestHelper.Center, downscaleLevel: 2);
    }

    [TestMethod]
    public void MovedZoneCapturesNewPosition()
    {
        using X11ScreenCaptureService service = new();
        HardwareTestHelper.AssertMovedZoneCapturesNewPosition(service);
    }

    [TestMethod]
    public void MovedDownscaledZoneCapturesNewPosition()
    {
        using X11ScreenCaptureService service = new();
        HardwareTestHelper.AssertMovedZoneCapturesNewPosition(service, downscaleLevel: 2);
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(2)]
    public void ZonesChangedDuringACaptureShowTheChange(int downscaleLevel)
    {
        using X11ScreenCaptureService service = new();
        HardwareTestHelper.AssertZonesChangedDuringACaptureShowTheChange(service, downscaleLevel);
    }

    [TestMethod]
    public void ZoneShowsTheLatestFrame()
    {
        using X11ScreenCaptureService service = new();
        HardwareTestHelper.AssertZoneShowsTheLatestFrame(service);
    }

    [TestMethod]
    public void ZonesCanBeChangedWhileCapturing()
    {
        using X11ScreenCaptureService service = new();
        HardwareTestHelper.AssertZonesCanBeChangedWhileCapturing(service);
    }

    [TestMethod]
    public void DownscaledZonesCanBeChangedWhileCapturing()
    {
        using X11ScreenCaptureService service = new();
        HardwareTestHelper.AssertZonesCanBeChangedWhileCapturing(service, downscaleLevel: 2);
    }

    [TestMethod]
    public void ConcurrentCapturesWork()
    {
        using X11ScreenCaptureService service = new();
        HardwareTestHelper.AssertConcurrentCapturesWork(service);
    }

    [TestMethod]
    public void ThreadSafetyContractHolds()
    {
        // Disposed by the test while everything runs; disposing it again is harmless.
        using X11ScreenCaptureService service = new();
        HardwareTestHelper.AssertThreadSafetyContract(service);
    }

    [TestMethod]
    public void EnumerationDuringDisposeIsSafe()
        => HardwareTestHelper.AssertEnumerationDuringDisposeIsSafe(() => new X11ScreenCaptureService());

    [TestMethod]
    public void PartialZonesMatchFullScreen()
    {
        using X11ScreenCaptureService service = new();
        HardwareTestHelper.AssertPartialZonesMatchFullScreen(service);
    }

    [TestMethod]
    public void DisposeIsRepeatable()
    {
        using X11ScreenCaptureService service = new();
        HardwareTestHelper.AssertDisposeIsRepeatable(service);
    }

    [TestMethod]
    public void FailedRestartLeavesNoHandles()
    {
        // The screen doesn't exist, so every restart fails after the connection was opened.
        Display display = HardwareTestHelper.CreateMissingDisplay();
        X11ScreenCapture screenCapture = new(display, X11CaptureOptions.Default, null);
        AssertNoHandles(screenCapture);

        CaptureZone<ColorBGRA> zone = screenCapture.RegisterCaptureZone(new CaptureRegion(0, 0, 64, 64));

        for (int i = 0; i < 3; i++)
            Assert.AreEqual(CaptureResult.Unavailable, screenCapture.CaptureScreen());
        AssertNoHandles(screenCapture);

        InvalidOperationException error = Assert.IsInstanceOfType<InvalidOperationException>(screenCapture.LastError);
        StringAssert.Contains(error.Message, "no screen 99");

        Assert.IsTrue(screenCapture.UnregisterCaptureZone(zone));

        screenCapture.Dispose();
        screenCapture.Dispose();
    }

    /// <summary>
    /// Restarts free the image while zones are copied from it on the capture thread.
    /// The zone update holds a reference on the image handle, so this must not read freed memory (which would crash the test host).
    /// </summary>
    [TestMethod]
    public void RestartWhileCapturingIsSafe()
    {
        const int restarts = 100;
        TimeSpan timeout = TimeSpan.FromSeconds(30);

        using X11ScreenCaptureService service = new();
        (_, Display display) = HardwareTestHelper.GetFirstDisplay(service);
        IScreenCapture screenCapture = service.GetScreenCapture(display);
        screenCapture.RegisterCaptureZone(new CaptureRegion(0, 0, display.Width, display.Height));
        screenCapture.RegisterCaptureZone(new CaptureRegion(0, 0, display.Width, display.Height), downscaleLevel: 2);
        HardwareTestHelper.CaptureFrame(screenCapture);

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
            for (int i = 0; i < restarts; i++)
                screenCapture.Restart();
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

        Assert.IsTrue(finished, "The restarts didn't finish (deadlock?).");
        Assert.IsTrue(capture.Wait(timeout), "The capture thread didn't finish (deadlock?).");
        Assert.IsGreaterThan(0, captures, "No capture ran during the restarts.");

        // Still works afterwards.
        HardwareTestHelper.CaptureFrame(screenCapture);
    }

    /// <summary>
    /// Reads the description of a real image. With wrongly sized <c>unsigned long</c> masks the format check reads padding and fails.
    /// </summary>
    [TestMethod]
    public void ImageDescriptionMatchesServer()
    {
        using X11DisplayHandle display = X11DisplayHandle.Open(null);
        Assert.IsFalse(display.IsInvalid, "Couldn't open the display.");

        CULong root = X11.XRootWindowOfScreen(X11.XScreenOfDisplay(display, 0));
        using X11ImageHandle image = X11.XGetImage(display, root, 0, 0, 3, 2, X11.ALL_PLANES, X11.ZPIXMAP);
        Assert.IsFalse(image.IsInvalid, "Couldn't get an image.");

        image.ReadDescription();

        Assert.AreEqual(3, image.Width);
        Assert.AreEqual(2, image.Height);
        Assert.IsGreaterThanOrEqualTo(3 * 4, image.Stride);
        Assert.IsTrue(image.IsBGRA, $"Unexpected format: {image.Format}");
    }

    /// <summary>
    /// Requests an image outside the root window (BadMatch). Xlib's default handler would terminate the test host.
    /// </summary>
    [TestMethod]
    public void XErrorIsRecordedInsteadOfTerminating()
    {
        const byte badMatch = 8;
        const byte getImage = 73;

        using X11DisplayHandle display = X11DisplayHandle.Open(null);
        Assert.IsFalse(display.IsInvalid, "Couldn't open the display.");

        nint screen = X11.XScreenOfDisplay(display, 0);
        CULong root = X11.XRootWindowOfScreen(screen);
        using X11ImageHandle image = X11.XGetImage(display, root, X11.XWidthOfScreen(screen) + 10, 0, 1, 1, X11.ALL_PLANES, X11.ZPIXMAP);

        Assert.IsTrue(image.IsInvalid);
        Assert.AreEqual(new X11Error(badMatch, getImage, 0), display.LastError);

        display.ClearError();
        Assert.IsNull(display.LastError);
    }

    /// <summary>
    /// The fourth byte of a depth-24 visual is padding that every server fills as it likes (Xvfb: 0, xorgxrdp: 255),
    /// and <c>XGetSubImage</c> sets it to 0 on every server (Xlib copies the pixels masked to the depth).
    /// </summary>
    [TestMethod]
    [DataRow(false, 0)]
    [DataRow(false, 2)]
    [DataRow(true, 0)]
    [DataRow(true, 2)]
    public void ZonesAreOpaque(bool onXvfb, int downscaleLevel)
    {
        using XvfbServer? server = onXvfb ? XvfbServer.Start() : null;
        using X11ScreenCaptureService service = new(displayName: server?.DisplayName);

        (_, Display display) = HardwareTestHelper.GetFirstDisplay(service);
        X11ScreenCapture screenCapture = service.GetScreenCapture(display);
        CaptureZone<ColorBGRA> zone = screenCapture.RegisterCaptureZone(new CaptureRegion(0, 0, display.Width, display.Height), downscaleLevel);

        HardwareTestHelper.CaptureFrame(screenCapture);

        using CaptureFrame<ColorBGRA> frame = zone.Lock();
        Assert.IsGreaterThan(0, frame.Pixels.Length);
        foreach (ColorBGRA pixel in frame.Pixels)
            Assert.AreEqual(byte.MaxValue, pixel.A);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void SharedMemoryIsUsedByDefault(bool onXvfb)
    {
        using XvfbServer? server = onXvfb ? XvfbServer.Start() : null;
        using X11ScreenCaptureService service = new(displayName: server?.DisplayName);

        (_, Display display) = HardwareTestHelper.GetFirstDisplay(service);
        X11ScreenCapture screenCapture = service.GetScreenCapture(display);
        Assert.IsTrue(screenCapture.UsesSharedMemory);

        HardwareTestHelper.CaptureFrame(screenCapture);
        Assert.IsTrue(screenCapture.UsesSharedMemory);
        Assert.IsNull(screenCapture.LastError);
    }

    [TestMethod]
    public void SharedMemoryCanBeTurnedOff()
    {
        using X11ScreenCaptureService service = new(new X11CaptureOptions { UseSharedMemory = false });
        HardwareTestHelper.AssertCapturesZone(service, HardwareTestHelper.Center);

        (_, Display display) = HardwareTestHelper.GetFirstDisplay(service);
        Assert.IsFalse(service.GetScreenCapture(display).UsesSharedMemory);
    }

    /// <summary>
    /// Captures the same patterns with and without shared memory. The second pattern shows that the segment gets the new frames.
    /// </summary>
    [TestMethod]
    public void SharedMemoryCapturesTheSameAsWithout()
    {
        using XvfbServer server = XvfbServer.Start();
        using X11ScreenCaptureService sharedService = new(displayName: server.DisplayName);
        using X11ScreenCaptureService unsharedService = new(new X11CaptureOptions { UseSharedMemory = false }, server.DisplayName);

        (_, Display display) = HardwareTestHelper.GetFirstDisplay(sharedService);
        X11ScreenCapture shared = sharedService.GetScreenCapture(display);
        X11ScreenCapture unshared = unsharedService.GetScreenCapture(display);
        Assert.IsTrue(shared.UsesSharedMemory);
        Assert.IsFalse(unshared.UsesSharedMemory);

        CaptureRegion region = new(0, 0, display.Width, display.Height);
        CaptureZone<ColorBGRA> sharedZone = shared.RegisterCaptureZone(region);
        CaptureZone<ColorBGRA> unsharedZone = unshared.RegisterCaptureZone(region);

        ColorBGRA[]? previous = null;
        foreach (int seed in (int[])[1, 2])
        {
            server.SetRootPattern(seed);
            HardwareTestHelper.CaptureFrame(shared);
            HardwareTestHelper.CaptureFrame(unshared);

            ColorBGRA[] expected = HardwareTestHelper.GetPixels(unsharedZone);
            CollectionAssert.AreEqual(expected, HardwareTestHelper.GetPixels(sharedZone), $"Pattern {seed} differs.");

            if (previous != null)
                CollectionAssert.AreNotEqual(previous, expected, "The patterns look the same.");
            previous = expected;
        }
    }

    /// <summary>
    /// Like a remote server: without the extension, or with one that can't attach this process's segments (BadAccess), the capture reads without shared memory and reports nothing.
    /// </summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ServerWithoutSharedMemoryIsReadWithoutIt(bool ownIpcNamespace)
    {
        int segments = CountOwnSegments();

        using XvfbServer server = XvfbServer.Start(sharedMemoryExtension: ownIpcNamespace, ownIpcNamespace: ownIpcNamespace);
        server.SetRootPattern(1);
        using X11ScreenCaptureService service = new(displayName: server.DisplayName);

        (_, Display display) = HardwareTestHelper.GetFirstDisplay(service);
        X11ScreenCapture screenCapture = service.GetScreenCapture(display);
        CaptureZone<ColorBGRA> zone = screenCapture.RegisterCaptureZone(new CaptureRegion(0, 0, display.Width, display.Height));

        HardwareTestHelper.CaptureFrame(screenCapture);
        Assert.IsFalse(screenCapture.UsesSharedMemory);
        Assert.IsNull(screenCapture.LastError);
        Assert.IsTrue(HardwareTestHelper.GetPixels(zone).Any(p => (p.R | p.G | p.B) != 0), "The pattern wasn't captured.");

        // The segment the server couldn't attach is gone right away.
        Assert.AreEqual(segments, CountOwnSegments(), "A segment is left.");
    }

    /// <summary>
    /// Every restart creates a new segment: the old ones and the last one are removed once they're detached.
    /// </summary>
    [TestMethod]
    public void SharedMemoryIsReleased()
    {
        int segments = CountOwnSegments();

        using (X11ScreenCaptureService service = new())
        {
            (_, Display display) = HardwareTestHelper.GetFirstDisplay(service);
            X11ScreenCapture screenCapture = service.GetScreenCapture(display);
            Assert.IsTrue(screenCapture.UsesSharedMemory);

            for (int i = 0; i < 5; i++)
                screenCapture.Restart();

            AssertOwnSegments(segments + 1);
        }

        AssertOwnSegments(segments);
    }

    [TestMethod]
    public void OutdatedDisplaySizeIsReadAgain()
    {
        using X11ScreenCaptureService service = new();
        (_, Display display) = HardwareTestHelper.GetFirstDisplay(service);

        // The capture reads the size of the screen when it starts. With the outdated size, XGetImage would fail with BadMatch (see XErrorIsRecordedInsteadOfTerminating).
        Display outdated = display with { Width = display.Width + 16, Height = display.Height / 2 };

        HardwareTestHelper.AssertOutdatedDisplayIsReadAgain(display, outdated, d => new X11ScreenCapture(d, X11CaptureOptions.Default, null));
    }

    /// <summary>
    /// Counts the System V shared memory segments this process created, including the removed ones that are still attached.
    /// </summary>
    private static int CountOwnSegments()
    {
        string processId = Environment.ProcessId.ToString(CultureInfo.InvariantCulture);

        // Columns: key shmid perms size cpid ...
        return File.ReadLines("/proc/sysvipc/shm").Skip(1).Count(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[4] == processId);
    }

    // The server detaches a segment when it gets to the request, after the capture closed its connection.
    private static void AssertOwnSegments(int expected)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        while ((CountOwnSegments() != expected) && (stopwatch.Elapsed < TimeSpan.FromSeconds(2)))
            Thread.Sleep(10);

        Assert.AreEqual(expected, CountOwnSegments(), "Shared memory segments of this process.");
    }

    private static void AssertNoHandles(X11ScreenCapture screenCapture)
    {
        List<FieldInfo> fields = [.. typeof(X11ScreenCapture).GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
                                                             .Where(f => typeof(SafeHandle).IsAssignableFrom(f.FieldType))];
        Assert.HasCount(2, fields, "Handle fields not found.");

        foreach (FieldInfo field in fields)
            Assert.IsNull(field.GetValue(screenCapture), $"{field.Name} isn't empty.");
    }
}

/// <summary>
/// Runs only where libX11 is installed but no X server is configured (e.g. a headless Linux machine or a Wayland session without XWayland).
/// </summary>
[TestClass]
[TestCategory(HardwareTestHelper.CATEGORY)]
[OSCondition(OperatingSystems.Linux)]
[SupportedOSPlatform("linux")]
public sealed class X11NoServerTests
{
    [TestMethod]
    public void ServiceThrowsWithoutXServer()
    {
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")))
            Assert.Inconclusive("An X server is configured ($DISPLAY is set).");

        if (!NativeLibrary.TryLoad("libX11.so.6", out nint library))
            Assert.Inconclusive("libX11 isn't installed.");
        NativeLibrary.Free(library);

        using X11ScreenCaptureService service = new();

        InvalidOperationException exception = Assert.ThrowsExactly<InvalidOperationException>(() => service.GetGraphicsCards());
        StringAssert.Contains(exception.Message, "$DISPLAY");

        Assert.ThrowsExactly<InvalidOperationException>(() => service.GetDisplays(HardwareTestHelper.CreateMissingDisplay().GraphicsCard));
    }

    [TestMethod]
    public void CaptureFailsWithoutXServer()
    {
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")))
            Assert.Inconclusive("An X server is configured ($DISPLAY is set).");

        if (!NativeLibrary.TryLoad("libX11.so.6", out nint library))
            Assert.Inconclusive("libX11 isn't installed.");
        NativeLibrary.Free(library);

        // The capture doesn't throw: it fails and tries to reconnect on every call.
        using X11ScreenCapture screenCapture = new(HardwareTestHelper.CreateMissingDisplay(index: 0), X11CaptureOptions.Default, null);
        screenCapture.RegisterCaptureZone(new CaptureRegion(0, 0, 64, 64));

        Assert.AreEqual(CaptureResult.Unavailable, screenCapture.CaptureScreen());
        Assert.AreEqual(CaptureResult.Unavailable, screenCapture.CaptureScreen());

        InvalidOperationException error = Assert.IsInstanceOfType<InvalidOperationException>(screenCapture.LastError);
        StringAssert.Contains(error.Message, "$DISPLAY");
    }
}
