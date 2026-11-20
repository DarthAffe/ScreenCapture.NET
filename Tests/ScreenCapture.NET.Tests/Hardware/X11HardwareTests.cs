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
public sealed partial class X11HardwareTests
{
    [TestMethod]
    [DataRow(true, DisplayName = "With RandR")]
    [DataRow(false, DisplayName = "Without RandR")]
    public void XvfbIsSupported(bool randrExtension)
    {
        using XvfbServer server = XvfbServer.Start(randrExtension: randrExtension);
        Assert.IsTrue(X11ScreenCaptureService.IsSupported(server.DisplayName));
    }

    [TestMethod]
    public void StoppedServerIsNotSupported()
    {
        using XvfbServer server = XvfbServer.Start();
        server.Kill();

        Assert.IsFalse(X11ScreenCaptureService.IsSupported(server.DisplayName));
    }

    /// <summary>
    /// The server of the session ($DISPLAY) is supported unless it's XWayland, which doesn't show the Wayland desktop.
    /// </summary>
    [TestMethod]
    public void SessionServerIsSupportedUnlessItsXWayland()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")))
            Assert.Inconclusive("No X server available ($DISPLAY is not set).");

        bool isXWayland;
        using (X11DisplayHandle display = X11DisplayHandle.Open(null))
        {
            if (display.IsInvalid) Assert.Inconclusive("The X server of the session can't be opened.");
            isXWayland = X11.XQueryExtension(display, "XWAYLAND", out _, out _, out _);
        }

        Assert.AreEqual(!isXWayland, X11ScreenCaptureService.IsSupported(), isXWayland ? "The session's X server is XWayland." : "The session's X server isn't XWayland.");
    }

    [TestMethod]
    public void EnumeratesDisplays()
    {
        using X11ScreenCaptureService service = X11DesktopServer.CreateService();
        HardwareTestHelper.AssertEnumeratesDisplays(service);
    }

    [TestMethod]
    public void DevicesHaveStableIds()
    {
        using X11ScreenCaptureService service = X11DesktopServer.CreateService();
        HardwareTestHelper.AssertDevicesHaveStableIds(service);
    }

    [TestMethod]
    public void OptionsAreUsedForCaptures()
    {
        X11CaptureOptions options = new();
        using X11ScreenCaptureService service = X11DesktopServer.CreateService(options);
        Assert.AreSame(options, service.Options);
        Assert.IsNull(service.DisplayName);

        (_, Display display) = HardwareTestHelper.GetFirstDisplay(service);
        X11ScreenCapture screenCapture = service.GetScreenCapture(display);
        Assert.AreSame(options, screenCapture.Options);

        // Equal options return the existing capture.
        Assert.AreSame(screenCapture, service.GetScreenCapture(display, new X11CaptureOptions()));
        Assert.ThrowsExactly<ArgumentNullException>(() => service.GetScreenCapture(display, null!));

        using X11ScreenCaptureService defaultService = X11DesktopServer.CreateService();
        Assert.AreSame(X11CaptureOptions.Default, defaultService.Options);
    }

    [TestMethod]
    public void ServiceConnectsToTheGivenDisplay()
    {
        X11DesktopServer.Require();
        string displayName = Environment.GetEnvironmentVariable("DISPLAY")!;

        using X11ScreenCaptureService defaultService = X11DesktopServer.CreateService();
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
        using (X11ScreenCaptureService defaultService = X11DesktopServer.CreateService())
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
        using X11ScreenCaptureService service = X11DesktopServer.CreateService();
        HardwareTestHelper.AssertDisplaysAreFoundById(service);
    }

    [TestMethod]
    public void DevicesAreLookedUpById()
    {
        using X11ScreenCaptureService service = X11DesktopServer.CreateService();
        HardwareTestHelper.AssertDevicesAreLookedUpById(service);
    }

    [TestMethod]
    public void DisposedCaptureIsReplaced()
    {
        using X11ScreenCaptureService service = X11DesktopServer.CreateService();
        HardwareTestHelper.AssertDisposedCaptureIsReplaced(service);
    }

    [TestMethod]
    public void ConcurrentRequestsGetTheSameCapture()
    {
        using X11ScreenCaptureService service = X11DesktopServer.CreateService();
        HardwareTestHelper.AssertConcurrentRequestsGetTheSameCapture(service);
    }

    [TestMethod]
    public void CapturesAreCachedById()
    {
        using X11ScreenCaptureService service = X11DesktopServer.CreateService();
        HardwareTestHelper.AssertCapturesAreCachedById(service);
    }

    [TestMethod]
    public void CapturesFullScreenZone()
    {
        using X11ScreenCaptureService service = X11DesktopServer.CreateService();
        HardwareTestHelper.AssertCapturesZone(service, HardwareTestHelper.FullScreen);
    }

    [TestMethod]
    public void CapturesPartialZone()
    {
        using X11ScreenCaptureService service = X11DesktopServer.CreateService();
        HardwareTestHelper.AssertCapturesZone(service, HardwareTestHelper.Center);
    }

    [TestMethod]
    public void CapturesDownscaledZone()
    {
        using X11ScreenCaptureService service = X11DesktopServer.CreateService();
        HardwareTestHelper.AssertCapturesZone(service, HardwareTestHelper.Center, downscaleLevel: 2);
    }

    [TestMethod]
    public void MovedZoneCapturesNewPosition()
    {
        using X11ScreenCaptureService service = X11DesktopServer.CreateService();
        HardwareTestHelper.AssertMovedZoneCapturesNewPosition(service);
    }

    [TestMethod]
    public void MovedDownscaledZoneCapturesNewPosition()
    {
        using X11ScreenCaptureService service = X11DesktopServer.CreateService();
        HardwareTestHelper.AssertMovedZoneCapturesNewPosition(service, downscaleLevel: 2);
    }

    [TestMethod]
    public void ZoneShowsTheLatestFrame()
    {
        using X11ScreenCaptureService service = X11DesktopServer.CreateService();
        HardwareTestHelper.AssertZoneShowsTheLatestFrame(service);
    }

    [TestMethod]
    public void ZonesCanBeChangedWhileCapturing()
    {
        using X11ScreenCaptureService service = X11DesktopServer.CreateService();
        HardwareTestHelper.AssertZonesCanBeChangedWhileCapturing(service);
    }

    [TestMethod]
    public void DownscaledZonesCanBeChangedWhileCapturing()
    {
        using X11ScreenCaptureService service = X11DesktopServer.CreateService();
        HardwareTestHelper.AssertZonesCanBeChangedWhileCapturing(service, downscaleLevel: 2);
    }

    [TestMethod]
    public void ConcurrentCapturesWork()
    {
        using X11ScreenCaptureService service = X11DesktopServer.CreateService();
        HardwareTestHelper.AssertConcurrentCapturesWork(service);
    }

    [TestMethod]
    public void ThreadSafetyContractHolds()
    {
        // Disposed by the test while everything runs; disposing it again is harmless.
        using X11ScreenCaptureService service = X11DesktopServer.CreateService();
        HardwareTestHelper.AssertThreadSafetyContract(service);
    }

    [TestMethod]
    public void EnumerationDuringDisposeIsSafe()
        => HardwareTestHelper.AssertEnumerationDuringDisposeIsSafe(() => X11DesktopServer.CreateService());

    [TestMethod]
    public void PartialZonesMatchFullScreen()
    {
        using X11ScreenCaptureService service = X11DesktopServer.CreateService();
        HardwareTestHelper.AssertPartialZonesMatchFullScreen(service);
    }

    [TestMethod]
    public void DisposeIsRepeatable()
    {
        using X11ScreenCaptureService service = X11DesktopServer.CreateService();
        HardwareTestHelper.AssertDisposeIsRepeatable(service);
    }

    [TestMethod]
    public void FailedRestartLeavesNoHandles()
    {
        // The display doesn't exist, so every restart fails after the connection was opened.
        using XvfbServer server = XvfbServer.Start();
        Display display = HardwareTestHelper.CreateMissingDisplay();
        X11ScreenCapture screenCapture = new(display, X11CaptureOptions.Default, server.DisplayName);
        AssertNoHandles(screenCapture);

        CaptureZone<ColorBGRA> zone = screenCapture.RegisterCaptureZone(new CaptureRegion(0, 0, 64, 64));

        for (int i = 0; i < 3; i++)
            Assert.AreEqual(CaptureResult.Unavailable, screenCapture.CaptureScreen());
        AssertNoHandles(screenCapture);

        InvalidOperationException error = Assert.IsInstanceOfType<InvalidOperationException>(screenCapture.LastError);
        StringAssert.Contains(error.Message, "'missing' isn't connected");

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

        using X11ScreenCaptureService service = X11DesktopServer.CreateService();
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
        using X11DisplayHandle display = X11DesktopServer.Open();
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

        using X11DisplayHandle display = X11DesktopServer.Open();
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
    /// A broken connection reads no monitors, so without a check the displays would be the whole screens with their last known size.
    /// </summary>
    [TestMethod]
    public void DisplaysOfBrokenConnectionAreNotRead()
    {
        using XvfbServer server = XvfbServer.Start();
        using X11DisplayHandle display = X11DisplayHandle.Open(server.DisplayName);
        Assert.IsFalse(display.IsInvalid, "Couldn't open the display.");
        Assert.AreEqual("screen", X11Displays.GetDisplays(display).Single().Display.DeviceName);

        server.Kill();
        InvalidOperationException exception = Assert.ThrowsExactly<InvalidOperationException>(() => X11Displays.GetDisplays(display));
        Assert.AreEqual($"Reading the displays failed: the connection to the X server '{server.DisplayName}' was lost.", exception.Message);
    }

    /// <summary>
    /// Xlib locks a broken connection for the thread that found it before it calls the handlers (see <see cref="X11ErrorHandler"/>):
    /// unless the lock is released, any other thread that uses or closes the connection waits forever.
    /// </summary>
    [TestMethod]
    public void BrokenConnectionCanBeClosedFromAnotherThread()
    {
        TimeSpan timeout = TimeSpan.FromSeconds(5);

        using XvfbServer server = XvfbServer.Start();
        X11DisplayHandle display = X11DisplayHandle.Open(server.DisplayName);
        Assert.IsFalse(display.IsInvalid, "Couldn't open the display.");
        Assert.IsFalse(display.IsConnectionLost);
        server.Kill();

        // Xlib compares pthread_t, which a new thread can reuse once a thread ended: the thread that found the broken connection lives until the other one is done.
        using ManualResetEventSlim found = new();
        using ManualResetEventSlim release = new();
        nuint foundOn = 0;
        Thread finder = new(() =>
        {
            foundOn = pthread_self();
            _ = X11.XSync(display, false);
            found.Set();
            release.Wait();
        }) { IsBackground = true };

        nuint closedOn = 0;
        Thread closer = new(() =>
        {
            closedOn = pthread_self();
            _ = X11.XSync(display, false);
            display.Dispose();
        }) { IsBackground = true };

        try
        {
            finder.Start();
            Assert.IsTrue(found.Wait(timeout), "The sync didn't return.");
            Assert.IsTrue(display.IsConnectionLost);
            Assert.IsNull(display.LastError);

            closer.Start();
            Assert.IsTrue(closer.Join(timeout), "Another thread couldn't use the broken connection.");
            Assert.AreNotEqual(foundOn, closedOn);
        }
        finally
        {
            release.Set();
        }
    }

    [LibraryImport("libc")]
    private static partial nuint pthread_self();

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
        using X11ScreenCaptureService service = X11DesktopServer.CreateService(displayName: server?.DisplayName);

        (_, Display display) = HardwareTestHelper.GetFirstDisplay(service);
        X11ScreenCapture screenCapture = service.GetScreenCapture(display);
        CaptureZone<ColorBGRA> zone = screenCapture.RegisterCaptureZone(new CaptureRegion(0, 0, display.Width, display.Height), downscaleLevel);

        HardwareTestHelper.CaptureFrame(screenCapture);

        using CaptureFrame<ColorBGRA> frame = zone.AcquireFrame();
        Assert.IsGreaterThan(0, frame.Pixels.Length);
        foreach (ColorBGRA pixel in frame.Pixels)
            Assert.AreEqual(byte.MaxValue, pixel.A);
    }

    /// <summary>
    /// A local server attaches the segment, so the server can write into it.
    /// </summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void SharedImageIsCreatedWhereTheServerCanShare(bool onXvfb)
    {
        using XvfbServer? server = onXvfb ? XvfbServer.Start("640x480x24") : null;
        using X11DisplayHandle display = server == null ? X11DesktopServer.Open() : X11DisplayHandle.Open(server.DisplayName);
        Assert.IsFalse(display.IsInvalid, "Couldn't open the display.");

        nint screen = X11.XScreenOfDisplay(display, 0);
        using X11ImageHandle? image = X11ImageHandle.CreateShared(display, screen, 320, 200);
        Assert.IsNotNull(image, "The server didn't attach the segment.");
        Assert.IsTrue(image.IsShared);
        Assert.AreEqual(320, image.Width);
        Assert.AreEqual(200, image.Height);
        Assert.IsTrue(image.IsBGRA, $"Unexpected format: {image.Format}");

        display.ClearError();
        Assert.IsTrue(X11.XShmGetImage(display, X11.XRootWindowOfScreen(screen), image, 0, 0, X11.ALL_PLANES));
        Assert.IsNull(display.LastError);
    }

    /// <summary>
    /// Like a remote server: without the extension, or with one that can't attach this process's segments (BadAccess), there's no shared image, no error and no segment left.
    /// </summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void SharedImageIsNotCreatedWhereTheServerCantShare(bool ownIpcNamespace)
    {
        int segments = CountOwnSegments();

        using XvfbServer server = XvfbServer.Start(sharedMemoryExtension: ownIpcNamespace, ownIpcNamespace: ownIpcNamespace);
        using X11DisplayHandle display = X11DisplayHandle.Open(server.DisplayName);
        Assert.IsFalse(display.IsInvalid, "Couldn't open the display.");

        Assert.IsNull(X11ImageHandle.CreateShared(display, X11.XScreenOfDisplay(display, 0), 320, 200));
        Assert.IsNull(display.LastError);

        // The segment the server couldn't attach is gone right away.
        Assert.AreEqual(segments, CountOwnSegments(), "A segment is left.");
    }

    [TestMethod]
    public void SharedMemoryCanBeTurnedOff()
    {
        using X11ScreenCaptureService service = X11DesktopServer.CreateService(new X11CaptureOptions { UseSharedMemory = false });
        HardwareTestHelper.AssertCapturesZone(service, HardwareTestHelper.Center);
    }

    /// <summary>
    /// Captures the same patterns with and without shared memory. The second pattern shows that the segment gets the new frames.
    /// Which call each option makes shows in the errors of <see cref="LostConnectionIsUnavailableUntilTheServerIsBack"/>.
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
    /// Like a remote server (see <see cref="SharedImageIsNotCreatedWhereTheServerCantShare"/>): the capture reads without shared memory and reports nothing.
    /// </summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ServerWithoutSharedMemoryIsReadWithoutIt(bool ownIpcNamespace)
    {
        using XvfbServer server = XvfbServer.Start(sharedMemoryExtension: ownIpcNamespace, ownIpcNamespace: ownIpcNamespace);
        server.SetRootPattern(1);
        using X11ScreenCaptureService service = new(displayName: server.DisplayName);

        (_, Display display) = HardwareTestHelper.GetFirstDisplay(service);
        X11ScreenCapture screenCapture = service.GetScreenCapture(display);
        CaptureZone<ColorBGRA> zone = screenCapture.RegisterCaptureZone(new CaptureRegion(0, 0, display.Width, display.Height));

        HardwareTestHelper.CaptureFrame(screenCapture);
        Assert.IsNull(screenCapture.LastError);
        Assert.IsTrue(HardwareTestHelper.GetPixels(zone).Any(p => (p.R | p.G | p.B) != 0), "The pattern wasn't captured.");
    }

    /// <summary>
    /// Every restart creates a new segment: the old ones and the last one are removed once they're detached.
    /// </summary>
    [TestMethod]
    public void SharedMemoryIsReleased()
    {
        int segments = CountOwnSegments();

        using (X11ScreenCaptureService service = X11DesktopServer.CreateService())
        {
            (_, Display display) = HardwareTestHelper.GetFirstDisplay(service);
            X11ScreenCapture screenCapture = service.GetScreenCapture(display);

            for (int i = 0; i < 5; i++)
                screenCapture.Restart();

            AssertOwnSegments(segments + 1);
        }

        AssertOwnSegments(segments);
    }

    [TestMethod]
    public void OutdatedDisplaySizeIsReadAgain()
    {
        using X11ScreenCaptureService service = X11DesktopServer.CreateService();
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

    /// <summary>
    /// Every RandR monitor is a display (incl. monitors without output, as <c>xrandr --setmonitor</c> defines them), with its name and its position on the screen.
    /// </summary>
    [TestMethod]
    public void DisplaysAreTheMonitors()
    {
        using XvfbServer server = XvfbServer.Start("640x480x24");
        server.Xrandr("--setmonitor", "left", "320/1x480/1+0+0", "none");
        server.Xrandr("--setmonitor", "corner", "160/1x120/1+480+360", "none");
        using X11ScreenCaptureService service = new(displayName: server.DisplayName);

        GraphicsCard graphicsCard = service.GetGraphicsCards().Single();
        Assert.AreEqual(new GraphicsCard(server.DisplayName, 0, server.DisplayName, 0, 0), graphicsCard);

        List<Display> displays = [.. service.GetDisplays(graphicsCard)];
        CollectionAssert.AreEqual(Enumerable.Range(0, displays.Count).ToList(), displays.Select(d => d.Index).ToList(), "The indices aren't the positions.");

        Display Expected(string name, int x, int y, int width, int height)
            => new($"{server.DisplayName}/0/{name}", 0, name, string.Empty, x, y, width, height, ImageRotation.None, graphicsCard);

        // Xvfb's own monitor is called "screen".
        CollectionAssert.AreEquivalent(new[] { Expected("left", 0, 0, 320, 480), Expected("corner", 480, 360, 160, 120), Expected("screen", 0, 0, 640, 480) },
                                       displays.Select(d => d with { Index = 0 }).ToList());
    }

    [TestMethod]
    public void ServerWithoutMonitorsHasOneDisplayPerScreen()
    {
        using XvfbServer server = XvfbServer.Start("640x480x24", randrExtension: false);
        server.SetRootPattern(1);
        using X11ScreenCaptureService service = new(displayName: server.DisplayName);

        GraphicsCard graphicsCard = service.GetGraphicsCards().Single();
        Display display = Assert.ContainsSingle(service.GetDisplays(graphicsCard));
        Assert.AreEqual(new Display($"{server.DisplayName}/0", 0, "Screen 0", string.Empty, 0, 0, 640, 480, ImageRotation.None, graphicsCard), display);

        HardwareTestHelper.AssertCapturesZone(service, HardwareTestHelper.FullScreen);
    }

    [TestMethod]
    public void FriendlyNameIsTheMonitorNameOfTheEdid()
    {
        using XvfbServer server = XvfbServer.Start();
        server.SetMonitorName("Test Panel");
        using X11ScreenCaptureService service = new(displayName: server.DisplayName);

        (_, Display display) = HardwareTestHelper.GetFirstDisplay(service);
        Assert.AreEqual("screen", display.DeviceName);
        Assert.AreEqual("Test Panel", display.FriendlyName);

        // The capture finds the same display, so its restart doesn't change it.
        X11ScreenCapture screenCapture = service.GetScreenCapture(display);
        Assert.AreEqual(display, screenCapture.Display);
    }

    /// <summary>
    /// A monitor is captured from its rectangle of the screen: compared to the capture of the whole screen (Xvfb's own monitor).
    /// </summary>
    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void MonitorsAreReadFromTheirPosition(bool useSharedMemory)
    {
        using XvfbServer server = XvfbServer.Start("640x480x24");
        server.Xrandr("--setmonitor", "left", "320/1x480/1+0+0", "none");
        server.Xrandr("--setmonitor", "inner", "200/1x150/1+100+50", "none");
        server.Xrandr("--setmonitor", "corner", "160/1x120/1+480+360", "none");
        server.SetRootPattern(1);
        using X11ScreenCaptureService service = new(new X11CaptureOptions { UseSharedMemory = useSharedMemory }, server.DisplayName);

        List<Display> displays = [.. HardwareTestHelper.GetAllDisplays(service)];
        Display screen = displays.Single(d => d.DeviceName == "screen");
        ColorBGRA[] screenPixels = CaptureWholeDisplay(service.GetScreenCapture(screen));

        foreach (Display monitor in displays.Where(d => d.DeviceName != "screen"))
        {
            X11ScreenCapture screenCapture = service.GetScreenCapture(monitor);

            CollectionAssert.AreEqual(HardwareTestHelper.Crop(screenPixels, screen.Width, monitor.X, monitor.Y, monitor.Width, monitor.Height), CaptureWholeDisplay(screenCapture),
                                      $"{monitor.DeviceName} doesn't show its part of the screen.");
        }
    }

    /// <summary>
    /// Monitors defined by a client change without any event (measured), so the capture notices it by checking the monitor with every capture.
    /// </summary>
    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void ChangedMonitorRestartsWithIt(bool useSharedMemory)
    {
        using XvfbServer server = XvfbServer.Start("640x480x24");
        server.Xrandr("--setmonitor", "box", "200/1x100/1+0+0", "none");
        server.SetRootPattern(1);
        using X11ScreenCaptureService service = new(new X11CaptureOptions { UseSharedMemory = useSharedMemory }, server.DisplayName);

        List<Display> displays = [.. HardwareTestHelper.GetAllDisplays(service)];
        ColorBGRA[] screenPixels = CaptureWholeDisplay(service.GetScreenCapture(displays.Single(d => d.DeviceName == "screen")));
        Display box = displays.Single(d => d.DeviceName == "box");

        X11ScreenCapture screenCapture = service.GetScreenCapture(box);
        List<ScreenCaptureDisplayChangedEventArgs> changes = [];
        screenCapture.DisplayChanged += (_, e) => changes.Add(e);

        CaptureZone<ColorBGRA> zone = screenCapture.RegisterCaptureZone(new CaptureRegion(0, 0, 200, 100));
        HardwareTestHelper.CaptureFrame(screenCapture);

        // Moved.
        server.Xrandr("--delmonitor", "box");
        server.Xrandr("--setmonitor", "box", "200/1x100/1+300+200", "none");
        HardwareTestHelper.CaptureFrame(screenCapture);

        Display moved = box with { X = 300, Y = 200 };
        CollectionAssert.AreEqual(new[] { new ScreenCaptureDisplayChangedEventArgs(box, moved) }, changes);
        CollectionAssert.AreEqual(HardwareTestHelper.Crop(screenPixels, 640, 300, 200, 200, 100), HardwareTestHelper.GetPixels(zone), "The capture doesn't show the moved monitor.");

        // Resized: the zone doesn't fit anymore.
        server.Xrandr("--delmonitor", "box");
        server.Xrandr("--setmonitor", "box", "100/1x50/1+300+200", "none");
        HardwareTestHelper.CaptureFrame(screenCapture);

        Assert.AreEqual(moved with { Width = 100, Height = 50 }, screenCapture.Display);
        Assert.HasCount(2, changes);
        Assert.IsFalse(zone.IsValid);
    }

    [TestMethod]
    public void RemovedMonitorIsUnavailableUntilItsBack()
    {
        using XvfbServer server = XvfbServer.Start("640x480x24");
        server.Xrandr("--setmonitor", "box", "200/1x100/1+100+100", "none");
        using X11ScreenCaptureService service = new(displayName: server.DisplayName);

        Display box = HardwareTestHelper.GetAllDisplays(service).Single(d => d.DeviceName == "box");
        X11ScreenCapture screenCapture = service.GetScreenCapture(box);
        screenCapture.RegisterCaptureZone(new CaptureRegion(0, 0, 200, 100));
        HardwareTestHelper.CaptureFrame(screenCapture);

        server.Xrandr("--delmonitor", "box");
        Assert.AreEqual(CaptureResult.Unavailable, screenCapture.CaptureScreen());

        InvalidOperationException error = Assert.IsInstanceOfType<InvalidOperationException>(screenCapture.LastError);
        StringAssert.Contains(error.Message, $"'{box.Id}' isn't connected");

        server.Xrandr("--setmonitor", "box", "200/1x100/1+100+100", "none");
        HardwareTestHelper.CaptureFrame(screenCapture);
        Assert.AreEqual(box, screenCapture.Display);
    }

    /// <summary>
    /// A connection the server breaks (here it's killed, like a crash or a restart) made Xlib's default I/O error handler end the process, here the test host.
    /// The capture is unavailable while the server is gone and captures again once it's back.
    /// </summary>
    /// <param name="randrExtension">With RandR the display is a monitor, and the monitor check runs into the broken connection first; without it the read of the screen does.</param>
    /// <param name="useSharedMemory">Whether the screen is read with <c>XShmGetImage</c> or <c>XGetSubImage</c>.</param>
    /// <param name="failedCall">The call that runs into the broken connection.</param>
    [TestMethod]
    [DataRow(true, true, "XRRGetMonitors")]
    [DataRow(false, true, "XShmGetImage")]
    [DataRow(false, false, "XGetSubImage")]
    public void LostConnectionIsUnavailableUntilTheServerIsBack(bool randrExtension, bool useSharedMemory, string failedCall)
    {
        using XvfbServer server = XvfbServer.Start(randrExtension: randrExtension);
        using X11ScreenCaptureService service = new(new X11CaptureOptions { UseSharedMemory = useSharedMemory }, server.DisplayName);

        (_, Display display) = HardwareTestHelper.GetFirstDisplay(service);
        X11ScreenCapture screenCapture = service.GetScreenCapture(display);
        List<Exception> errors = [];
        screenCapture.ErrorOccurred += (_, e) => errors.Add(e.Exception);
        screenCapture.RegisterCaptureZone(new CaptureRegion(0, 0, 64, 64));
        HardwareTestHelper.CaptureFrame(screenCapture);

        server.Kill();
        Assert.AreEqual(CaptureResult.Unavailable, screenCapture.CaptureScreen());

        // It found the broken connection, then couldn't connect again.
        Assert.HasCount(2, errors, $"Errors: {string.Join(" | ", errors)}");
        Assert.AreEqual($"{failedCall} failed: the connection to the X server was lost.", errors[0].Message);
        StringAssert.Contains(errors[1].Message, $"Couldn't connect to the X server '{server.DisplayName}'");

        server.Restart();
        Stopwatch stopwatch = Stopwatch.StartNew();
        CaptureResult result;
        while (((result = screenCapture.CaptureScreen()) != CaptureResult.NewFrame) && (stopwatch.Elapsed < TimeSpan.FromSeconds(5)))
            Thread.Sleep(20);

        Assert.AreEqual(CaptureResult.NewFrame, result, $"The capture didn't come back: {screenCapture.LastError}");
        Assert.AreEqual(display, screenCapture.Display);
    }

    /// <summary>
    /// Closing the connection (<c>XCloseDisplay</c>) syncs with the server, so it runs into a connection that broke since the last capture.
    /// </summary>
    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void CaptureOfLostServerCanBeDisposed(bool useSharedMemory)
    {
        using XvfbServer server = XvfbServer.Start();
        using X11ScreenCaptureService service = new(new X11CaptureOptions { UseSharedMemory = useSharedMemory }, server.DisplayName);

        (_, Display display) = HardwareTestHelper.GetFirstDisplay(service);
        X11ScreenCapture screenCapture = service.GetScreenCapture(display);
        screenCapture.RegisterCaptureZone(new CaptureRegion(0, 0, 64, 64));
        HardwareTestHelper.CaptureFrame(screenCapture);

        server.Kill();
        screenCapture.Dispose();
    }

    private static ColorBGRA[] CaptureWholeDisplay(X11ScreenCapture screenCapture)
    {
        Display display = screenCapture.Display;
        CaptureZone<ColorBGRA> zone = screenCapture.RegisterCaptureZone(new CaptureRegion(0, 0, display.Width, display.Height));
        HardwareTestHelper.CaptureFrame(screenCapture);

        ColorBGRA[] pixels = HardwareTestHelper.GetPixels(zone);
        screenCapture.UnregisterCaptureZone(zone);
        return pixels;
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

        using X11ScreenCaptureService service = X11DesktopServer.CreateService();

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
