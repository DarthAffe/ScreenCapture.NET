extern alias WGC;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.Versioning;
using HPPH;
using Vortice.DXGI;
using WGC::ScreenCapture.NET;

namespace ScreenCapture.NET.Tests.Hardware;

/// <summary>
/// The Windows.Graphics.Capture backend. On Windows 10 Windows shows a border around every display while it's captured.
/// </summary>
[TestClass]
[TestCategory(HardwareTestHelper.CATEGORY)]
[OSCondition(OperatingSystems.Windows)]
[SupportedOSPlatform("windows")]
public sealed class WGCHardwareTests
{
    [TestMethod]
    public void IsSupported() => Assert.IsTrue(WGCScreenCaptureService.IsSupported());

    [TestMethod]
    public void EnumeratesDisplays()
    {
        using WGCScreenCaptureService service = new();
        HardwareTestHelper.AssertEnumeratesDisplays(service);
    }

    [TestMethod]
    public void DisplaysAreTheOnesOfDX11()
    {
        using WGCScreenCaptureService service = new();
        using DX11ScreenCaptureService reference = new();

        CollectionAssert.AreEqual(HardwareTestHelper.GetAllDisplays(reference).ToArray(), HardwareTestHelper.GetAllDisplays(service).ToArray());
    }

    [TestMethod]
    public void DevicesHaveStableIds()
    {
        using WGCScreenCaptureService service = new();
        HardwareTestHelper.AssertDevicesHaveStableIds(service);
    }

    [TestMethod]
    public void DisplaysAreFoundById()
    {
        using WGCScreenCaptureService service = new();
        HardwareTestHelper.AssertDisplaysAreFoundById(service);
    }

    [TestMethod]
    public void DevicesAreLookedUpById()
    {
        using WGCScreenCaptureService service = new();
        HardwareTestHelper.AssertDevicesAreLookedUpById(service);
    }

    [TestMethod]
    public void DisposedCaptureIsReplaced()
    {
        using WGCScreenCaptureService service = new();
        HardwareTestHelper.AssertDisposedCaptureIsReplaced(service);
    }

    [TestMethod]
    public void ConcurrentRequestsGetTheSameCapture()
    {
        using WGCScreenCaptureService service = new();
        HardwareTestHelper.AssertConcurrentRequestsGetTheSameCapture(service);
    }

    [TestMethod]
    public void CapturesAreCachedById()
    {
        using WGCScreenCaptureService service = new();
        HardwareTestHelper.AssertCapturesAreCachedById(service);
    }

    [TestMethod]
    public void OptionsAreUsedForCaptures()
    {
        WGCCaptureOptions options = new() { Timeout = TimeSpan.FromMilliseconds(250) };
        using WGCScreenCaptureService service = new(options);
        Assert.AreSame(options, service.Options);

        (_, Display display) = HardwareTestHelper.GetFirstDisplay(service);
        WGCScreenCapture screenCapture = service.GetScreenCapture(display);
        Assert.AreSame(options, screenCapture.Options);
        Assert.AreEqual(TimeSpan.FromMilliseconds(250), screenCapture.Timeout);

        // Equal options return the existing capture, other ones throw.
        Assert.AreSame(screenCapture, service.GetScreenCapture(display, new WGCCaptureOptions { Timeout = TimeSpan.FromMilliseconds(250) }));
        Assert.ThrowsExactly<InvalidOperationException>(() => service.GetScreenCapture(display, WGCCaptureOptions.Default));
        Assert.ThrowsExactly<ArgumentNullException>(() => service.GetScreenCapture(display, null!));

        using WGCScreenCaptureService defaultService = new();
        Assert.AreSame(WGCCaptureOptions.Default, defaultService.Options);
    }

    [TestMethod]
    public void BorderIsOffByDefault() => Assert.IsFalse(WGCCaptureOptions.Default.ShowBorder);

    /// <summary>
    /// On Windows 11 the yellow border around a captured display is only drawn with <see cref="WGCCaptureOptions.ShowBorder"/>.
    /// It isn't part of the frames of the capture, so DX11 looks for it at the top edge of the first display.
    /// </summary>
    [TestMethod]
    public void BorderIsOnlyDrawnWithTheOption()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 20348))
        {
            Assert.Inconclusive("Windows 10 always draws the border.");
            return;
        }

        // The test processes of both TFMs run at the same time: the border drawn for the other one would be counted too.
        using Mutex otherProcesses = new(false, "ScreenCapture.NET.Tests.WGCBorder");
        otherProcesses.WaitOne();
        try
        {
            using DX11ScreenCaptureService reference = new(new DX11CaptureOptions { Timeout = TimeSpan.FromMilliseconds(100) });
            Display display = HardwareTestHelper.GetAllDisplays(reference).First();
            int withoutCapture = CountBorderPixels(reference, display);

            Assert.IsLessThan(withoutCapture + (display.Width / 10), CountBorderPixelsWhileCaptured(reference, display, showBorder: false),
                              $"{display.DeviceName}: the border is drawn without the option.");
            Assert.IsGreaterThan(withoutCapture + display.Width, CountBorderPixelsWhileCaptured(reference, display, showBorder: true),
                                 $"{display.DeviceName}: the border isn't drawn with the option.");
        }
        finally
        {
            otherProcesses.ReleaseMutex();
        }
    }

    private static int CountBorderPixelsWhileCaptured(DX11ScreenCaptureService reference, Display display, bool showBorder)
    {
        using WGCScreenCaptureService service = new(new WGCCaptureOptions { ShowBorder = showBorder });
        WGCScreenCapture screenCapture = service.GetScreenCapture(display);
        screenCapture.RegisterCaptureZone(new CaptureRegion(0, 0, 16, 16));
        HardwareTestHelper.CaptureFrame(screenCapture);

        // Windows draws the border shortly after the capture started.
        Thread.Sleep(500);
        return CountBorderPixels(reference, display);
    }

    /// <summary>
    /// Counts the yellow pixels in the top 3 rows of a display.
    /// </summary>
    private static int CountBorderPixels(DX11ScreenCaptureService reference, Display display)
    {
        DX11ScreenCapture screenCapture = reference.GetScreenCapture(display);
        CaptureZone<ColorBGRA> zone = screenCapture.RegisterCaptureZone(new CaptureRegion(0, 0, display.Width, 3));

        // Desktop Duplication shows changes only with the next frames: a few captures make sure the border (or its absence) is in the zone.
        for (int i = 0; i < 10; i++)
            screenCapture.CaptureScreen();

        int count = HardwareTestHelper.GetPixels(zone).Count(c => (c.R > 200) && (c.G > 150) && (c.B < 80));
        screenCapture.UnregisterCaptureZone(zone);
        return count;
    }

    [TestMethod]
    public void CapturesFullScreenZone()
    {
        using WGCScreenCaptureService service = new();
        HardwareTestHelper.AssertCapturesZone(service, HardwareTestHelper.FullScreen);
    }

    [TestMethod]
    public void CapturesPartialZone()
    {
        using WGCScreenCaptureService service = new();
        HardwareTestHelper.AssertCapturesZone(service, HardwareTestHelper.Center);
    }

    [TestMethod]
    public void CapturesDownscaledZone()
    {
        using WGCScreenCaptureService service = new();
        HardwareTestHelper.AssertCapturesZone(service, HardwareTestHelper.Center, downscaleLevel: 2);
    }

    [TestMethod]
    public void TimestampIsAStopwatchTimestamp()
    {
        using WGCScreenCaptureService service = new();
        (_, Display display) = HardwareTestHelper.GetFirstDisplay(service);
        IScreenCapture screenCapture = service.GetScreenCapture(display);

        ScreenCaptureUpdatedEventArgs args = default;
        screenCapture.Updated += (_, e) => args = e;

        long before = Stopwatch.GetTimestamp();
        HardwareTestHelper.CaptureFrame(screenCapture);
        long now = Stopwatch.GetTimestamp();

        // SystemRelativeTime is a QueryPerformanceCounter time: on another clock (or in other units) it would be far off.
        // It's when the frame is shown, which can be a refresh after the capture got it; the first frame can be older than the capture.
        Assert.AreEqual(CaptureResult.NewFrame, args.Result);
        Assert.IsGreaterThan(0, args.FrameNumber);
        Assert.IsInRange(before - (Stopwatch.Frequency * 5), now + (Stopwatch.Frequency / 10), args.Timestamp);
    }

    [TestMethod]
    public void NoNewFrameIsNoFailure()
    {
        using WGCScreenCaptureService service = new();
        (_, Display display) = HardwareTestHelper.GetFirstDisplay(service);
        WGCScreenCapture screenCapture = service.GetScreenCapture(display);
        HardwareTestHelper.CaptureFrame(screenCapture);

        // Without waiting, most calls find no new frame (unless something renders at a high rate). Neither may count as a failure.
        screenCapture.Timeout = TimeSpan.Zero;
        CaptureResult[] results = [.. Enumerable.Range(0, 50).Select(_ => screenCapture.CaptureScreen())];

        CollectionAssert.IsSubsetOf(results.Distinct().ToArray(), new[] { CaptureResult.NewFrame, CaptureResult.NoChange, CaptureResult.Timeout },
                                    $"Unexpected results: {string.Join(", ", results.Distinct())}");
    }

    [TestMethod]
    public void ZeroTimeoutDoesntWait()
    {
        using WGCScreenCaptureService service = new();
        (_, Display display) = HardwareTestHelper.GetFirstDisplay(service);
        WGCScreenCapture screenCapture = service.GetScreenCapture(display);

        // The first frame arrives a moment after the capture started.
        HardwareTestHelper.CaptureFrame(screenCapture);
        screenCapture.Timeout = TimeSpan.Zero;

        Stopwatch stopwatch = Stopwatch.StartNew();
        CaptureResult[] results = [.. Enumerable.Range(0, 10).Select(_ => screenCapture.CaptureScreen())];
        stopwatch.Stop();

        Assert.IsLessThan(TimeSpan.FromSeconds(1), stopwatch.Elapsed);
        Assert.Contains(CaptureResult.Timeout, results, $"Results: {string.Join(", ", results)}");
    }

    /// <summary>
    /// Without a new frame, a zone registered since is written from the last frame (kept in the capture texture),
    /// and zones that already show it aren't written again.
    /// </summary>
    [TestMethod]
    public void ZoneAddedOnAStaticScreenGetsTheLastFrame()
    {
        using WGCScreenCaptureService service = new();
        (_, Display display) = HardwareTestHelper.GetFirstDisplay(service);
        WGCScreenCapture screenCapture = service.GetScreenCapture(display);

        (int x, int y, int width, int height) = HardwareTestHelper.Center(display);
        CaptureRegion region = new(x, y, width, height);
        CaptureZone<ColorBGRA> reference = screenCapture.RegisterCaptureZone(region);

        int referenceWrites = 0;
        reference.Updated += (_, _) => referenceWrites++;

        HardwareTestHelper.CaptureFrame(screenCapture);
        screenCapture.Timeout = TimeSpan.Zero;

        // Something on the screen might render (cursor blinking, clock); try until a capture finds no new frame.
        for (int i = 0; i < 50; i++)
        {
            CaptureZone<ColorBGRA> added = screenCapture.RegisterCaptureZone(region);
            bool addedWritten = false;
            added.Updated += (_, _) => addedWritten = true;

            int writesBefore = referenceWrites;
            CaptureResult result = screenCapture.CaptureScreen();
            if (result is CaptureResult.NoChange or CaptureResult.Timeout)
            {
                Assert.IsTrue(addedWritten, "The new zone wasn't written.");
                Assert.AreEqual(writesBefore, referenceWrites, "The zone showing the last frame was written again.");
                CollectionAssert.AreEqual(HardwareTestHelper.GetPixels(reference), HardwareTestHelper.GetPixels(added));
                return;
            }

            Assert.AreEqual(CaptureResult.NewFrame, result);
            screenCapture.UnregisterCaptureZone(added);
        }

        Assert.Inconclusive("Every capture found a new frame: the screen never stood still.");
    }

    [TestMethod]
    public void MovedZoneCapturesNewPosition()
    {
        using WGCScreenCaptureService service = new();
        HardwareTestHelper.AssertMovedZoneCapturesNewPosition(service);
    }

    [TestMethod]
    public void MovedDownscaledZoneCapturesNewPosition()
    {
        using WGCScreenCaptureService service = new();
        HardwareTestHelper.AssertMovedZoneCapturesNewPosition(service, downscaleLevel: 2);
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(2)]
    public void ZonesChangedDuringACaptureShowTheChange(int downscaleLevel)
    {
        using WGCScreenCaptureService service = new();

        // The held capture doesn't wait for a frame: the zones are changed after it got one.
        HardwareTestHelper.AssertZonesChangedDuringACaptureShowTheChange(service, downscaleLevel, c => ((WGCScreenCapture)c).Timeout = TimeSpan.Zero);
    }

    [TestMethod]
    public void ZoneShowsTheLatestFrame()
    {
        using WGCScreenCaptureService service = new();
        HardwareTestHelper.AssertZoneShowsTheLatestFrame(service);
    }

    [TestMethod]
    public void ZonesCanBeChangedWhileCapturing()
    {
        using WGCScreenCaptureService service = new();
        HardwareTestHelper.AssertZonesCanBeChangedWhileCapturing(service);
    }

    [TestMethod]
    public void DownscaledZonesCanBeChangedWhileCapturing()
    {
        using WGCScreenCaptureService service = new();
        HardwareTestHelper.AssertZonesCanBeChangedWhileCapturing(service, downscaleLevel: 2);
    }

    [TestMethod]
    public void PartialZonesMatchFullScreen()
    {
        using WGCScreenCaptureService service = new();
        HardwareTestHelper.AssertPartialZonesMatchFullScreen(service);
    }

    /// <summary>
    /// <b>[HW]</b> Only meaningful with a rotated display: the frames of WGC are in the orientation of the display (no rotation by the capture).
    /// </summary>
    [TestMethod]
    public void MatchesDX9Orientation()
    {
        using WGCScreenCaptureService service = new();
        using DX9ScreenCaptureService reference = new();
        HardwareTestHelper.AssertMatchesReferenceOrientation(service, reference);
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(3)]
    public void DownscaledZonesMatchDX9(int downscaleLevel)
    {
        using WGCScreenCaptureService service = new();
        using DX9ScreenCaptureService reference = new();
        HardwareTestHelper.AssertDownscaledZonesMatchReference(service, reference, downscaleLevel);
    }

    /// <summary>
    /// A pattern of SDR colors in a window is captured with its exact values on every display that doesn't show HDR
    /// (HDR displays are converted by Windows, see 6.7). Shows a small window in the center of every display for a moment.
    /// </summary>
    [TestMethod]
    public void CapturesExactColors()
    {
        const int width = 256;
        const int height = 16;

        using WGCScreenCaptureService service = new(new WGCCaptureOptions { Timeout = TimeSpan.FromMilliseconds(100) });
        ColorBGRA[] pattern = DX11HardwareTests.CreateSdrPattern(width, height);

        int checkedDisplays = 0;
        foreach (Display display in HardwareTestHelper.GetAllDisplays(service))
        {
            if (DX11HardwareTests.IsShowingHdr(display)) continue;

            int x = (display.Width - width) / 2;
            int y = (display.Height - height) / 2;
            using TestPatternWindow window = new(display.X + x, display.Y + y, width, height, pattern);

            WGCScreenCapture screenCapture = service.GetScreenCapture(display);
            Assert.IsNull(screenCapture.LastError, $"{display.DeviceName}: {screenCapture.LastError}");

            CaptureZone<ColorBGRA> zone = screenCapture.RegisterCaptureZone(new CaptureRegion(x, y, width, height));
            ColorBGRA[] pixels;
            Stopwatch stopwatch = Stopwatch.StartNew();
            do
            {
                screenCapture.CaptureScreen();
                pixels = HardwareTestHelper.GetPixels(zone);
            } while ((DX11HardwareTests.Compare(pattern, pixels).MaxDifference > 0) && (stopwatch.Elapsed < TimeSpan.FromSeconds(3)));

            (int maxDifference, int differentPixels) = DX11HardwareTests.Compare(pattern, pixels);
            Assert.AreEqual(0, maxDifference, $"{display.DeviceName}: {differentPixels} pixels differ, by up to {maxDifference} (Night light or color management on?).");
            checkedDisplays++;
        }

        if (checkedDisplays == 0)
            Assert.Inconclusive("Every display shows HDR.");
    }

    /// <summary>
    /// A capture after a pause shows the screen as it is, not a frame from before the pause: Windows drops new frames while the frame pool has no free buffer,
    /// so the capture mustn't leave frames in it between captures. Shows two small windows in the center of a display one after another.
    /// </summary>
    [TestMethod]
    public void CaptureAfterAPauseShowsTheNewestFrame()
    {
        const int width = 256;
        const int height = 16;

        using WGCScreenCaptureService service = new();
        Display? sdrDisplay = HardwareTestHelper.GetAllDisplays(service).Cast<Display?>().FirstOrDefault(d => !DX11HardwareTests.IsShowingHdr(d!.Value));
        if (sdrDisplay is not { } display)
        {
            Assert.Inconclusive("Every display shows HDR.");
            return;
        }

        ColorBGRA[] first = DX11HardwareTests.CreateSdrPattern(width, height);
        ColorBGRA[] second = [.. first.Select(c => new ColorBGRA((byte)(255 - c.B), (byte)(255 - c.G), (byte)(255 - c.R), 255))];
        int x = (display.Width - width) / 2;
        int y = (display.Height - height) / 2;

        WGCScreenCapture screenCapture = service.GetScreenCapture(display);
        CaptureZone<ColorBGRA> zone = screenCapture.RegisterCaptureZone(new CaptureRegion(x, y, width, height));
        HardwareTestHelper.CaptureFrame(screenCapture);

        // No captures meanwhile: the frames showing the first window (and its appearance) arrive while nobody takes them.
        using (new TestPatternWindow(display.X + x, display.Y + y, width, height, first))
            Thread.Sleep(300);

        using TestPatternWindow window = new(display.X + x, display.Y + y, width, height, second);
        Thread.Sleep(300);

        Assert.AreEqual(CaptureResult.NewFrame, screenCapture.CaptureScreen());
        (int maxDifference, int differentPixels) = DX11HardwareTests.Compare(second, HardwareTestHelper.GetPixels(zone));
        Assert.AreEqual(0, maxDifference, $"{display.DeviceName}: the capture doesn't show the second window ({differentPixels} pixels differ, by up to {maxDifference}).");
    }

    [TestMethod]
    public void DisposeIsRepeatable()
    {
        using WGCScreenCaptureService service = new();
        HardwareTestHelper.AssertDisposeIsRepeatable(service);
    }

    [TestMethod]
    public void FailedRestartLeavesNoObjects()
    {
        DXGI.CreateDXGIFactory1(out IDXGIFactory1? factory).CheckError();
        using IDXGIFactory1 _ = factory!;

        // The display isn't connected, so every restart fails.
        using WGCScreenCapture screenCapture = new(factory!, HardwareTestHelper.CreateMissingDisplay(), WGCCaptureOptions.Default);
        AssertNoObjects(screenCapture);

        // Zones can be registered while the capture isn't running; they're filled once it (re)starts.
        CaptureZone<ColorBGRA> zone = screenCapture.RegisterCaptureZone(new CaptureRegion(0, 0, 64, 64));

        Assert.AreEqual(CaptureResult.Unavailable, screenCapture.CaptureScreen());
        AssertNoObjects(screenCapture);

        Assert.IsTrue(screenCapture.UnregisterCaptureZone(zone));
    }

    [TestMethod]
    public void DisposedCaptureLeavesNoObjects()
    {
        using WGCScreenCaptureService service = new();
        (_, Display display) = HardwareTestHelper.GetFirstDisplay(service);
        WGCScreenCapture screenCapture = service.GetScreenCapture(display);
        screenCapture.RegisterCaptureZone(new CaptureRegion(0, 0, 64, 64), downscaleLevel: 1);
        HardwareTestHelper.CaptureFrame(screenCapture);

        // A window shows up, so a frame arrives that no capture takes anymore.
        using (new TestPatternWindow(display.X, display.Y, 16, 16, new ColorBGRA[16 * 16]))
            Thread.Sleep(200);

        screenCapture.Dispose();

        AssertNoObjects(screenCapture);
    }

    [TestMethod]
    public void MissingDisplayIsReported()
    {
        DXGI.CreateDXGIFactory1(out IDXGIFactory1? factory).CheckError();
        using IDXGIFactory1 _ = factory!;

        using WGCScreenCapture screenCapture = new(factory!, HardwareTestHelper.CreateMissingDisplay(), WGCCaptureOptions.Default);

        InvalidOperationException error = Assert.IsInstanceOfType<InvalidOperationException>(screenCapture.LastError);
        StringAssert.Contains(error.Message, "'missing' isn't connected");
        Assert.AreEqual(CaptureResult.Unavailable, screenCapture.CaptureScreen());
    }

    [TestMethod]
    public void OutdatedDisplayIsReadAgain()
    {
        using WGCScreenCaptureService service = new();
        Display display = HardwareTestHelper.GetAllDisplays(service).Last();

        DXGI.CreateDXGIFactory1(out IDXGIFactory1? factory).CheckError();
        using IDXGIFactory1 _ = factory!;

        // Indices that don't exist (the capture finds the display by its id), another position, size and rotation.
        Display outdated = display with
        {
            Index = 99,
            X = display.X + 7,
            Width = display.Width / 2,
            Height = display.Height / 2,
            Rotation = display.Rotation == ImageRotation.None ? ImageRotation.Rotate90 : ImageRotation.None,
            GraphicsCard = display.GraphicsCard with { Index = 99 }
        };

        HardwareTestHelper.AssertOutdatedDisplayIsReadAgain(display, outdated, d => new WGCScreenCapture(factory!, d, WGCCaptureOptions.Default));
    }

    [TestMethod]
    public void RestartDuringACaptureWritesNoEmptyZone()
    {
        using WGCScreenCaptureService service = new();
        List<Display> displays = [.. HardwareTestHelper.GetAllDisplays(service)];

        DXGI.CreateDXGIFactory1(out IDXGIFactory1? factory).CheckError();
        using IDXGIFactory1 _ = factory!;

        foreach (Display display in displays)
            if (HardwareTestHelper.TryAssertRestartDuringACaptureWritesNoEmptyZone(display, display with { Width = display.Width / 2, Height = display.Height / 2 },
                                                                               d => new WGCScreenCapture(factory!, d, WGCCaptureOptions.Default)))
                return;

        Assert.Inconclusive("No display had a new frame for the first capture.");
    }

    [TestMethod]
    public void ConcurrentCapturesWork()
    {
        // A short timeout, so the threads take turns often.
        using WGCScreenCaptureService service = new(new WGCCaptureOptions { Timeout = TimeSpan.FromMilliseconds(10) });
        HardwareTestHelper.AssertConcurrentCapturesWork(service);
    }

    [TestMethod]
    public void ThreadSafetyContractHolds()
    {
        // A short timeout, so the capturing threads take turns often.
        // Disposed by the test while everything runs; disposing it again is harmless.
        using WGCScreenCaptureService service = new(new WGCCaptureOptions { Timeout = TimeSpan.FromMilliseconds(10) });
        HardwareTestHelper.AssertThreadSafetyContract(service);
    }

    [TestMethod]
    public void EnumerationDuringDisposeIsSafe()
        => HardwareTestHelper.AssertEnumerationDuringDisposeIsSafe(() => new WGCScreenCaptureService());

    private static void AssertNoObjects(WGCScreenCapture screenCapture)
    {
        // The factory belongs to the service.
        DX11HardwareTests.AssertNoDirectXFields(screenCapture, 2, "_factory");
        DX11HardwareTests.AssertZonePipelineReleased(screenCapture);

        foreach (string name in new[] { "_direct3DDevice", "_item", "_framePool", "_session", "_frameArrivedToken", "_itemClosedToken" })
        {
            FieldInfo field = typeof(WGCScreenCapture).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic) ?? throw new InvalidOperationException($"{name} not found.");
            Assert.IsNull(field.GetValue(screenCapture), $"{name} wasn't reset.");
        }

        FieldInfo newestFrame = typeof(WGCScreenCapture).GetField("_newestFrame", BindingFlags.Instance | BindingFlags.NonPublic) ?? throw new InvalidOperationException("_newestFrame not found.");
        Assert.AreEqual(nint.Zero, newestFrame.GetValue(screenCapture), "_newestFrame wasn't released.");
    }
}
