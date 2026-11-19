using System.Diagnostics;
using System.Reflection;
using System.Runtime.Versioning;
using HPPH;
using SharpGen.Runtime;
using Vortice.DXGI;

namespace ScreenCapture.NET.Tests.Hardware;

[TestClass]
[TestCategory(HardwareTestHelper.CATEGORY)]
[OSCondition(OperatingSystems.Windows)]
[SupportedOSPlatform("windows")]
public sealed class DX11HardwareTests
{
    [TestMethod]
    public void EnumeratesDisplays()
    {
        using DX11ScreenCaptureService service = new();
        HardwareTestHelper.AssertEnumeratesDisplays(service);
    }

    [TestMethod]
    public void DevicesHaveStableIds()
    {
        using DX11ScreenCaptureService service = new();
        HardwareTestHelper.AssertDevicesHaveStableIds(service);
    }

    [TestMethod]
    public void DisplaysAreFoundById()
    {
        using DX11ScreenCaptureService service = new();
        HardwareTestHelper.AssertDisplaysAreFoundById(service);
    }

    [TestMethod]
    public void DevicesAreLookedUpById()
    {
        using DX11ScreenCaptureService service = new();
        HardwareTestHelper.AssertDevicesAreLookedUpById(service);
    }

    [TestMethod]
    public void DisposedCaptureIsReplaced()
    {
        using DX11ScreenCaptureService service = new();
        HardwareTestHelper.AssertDisposedCaptureIsReplaced(service);
    }

    [TestMethod]
    public void ConcurrentRequestsGetTheSameCapture()
    {
        using DX11ScreenCaptureService service = new();
        HardwareTestHelper.AssertConcurrentRequestsGetTheSameCapture(service);
    }

    [TestMethod]
    public void CapturesAreCachedById()
    {
        using DX11ScreenCaptureService service = new();
        HardwareTestHelper.AssertCapturesAreCachedById(service);
    }

    [TestMethod]
    public void CapturesFullScreenZone()
    {
        using DX11ScreenCaptureService service = new();
        HardwareTestHelper.AssertCapturesZone(service, HardwareTestHelper.FullScreen);
    }

    [TestMethod]
    public void CapturesPartialZone()
    {
        using DX11ScreenCaptureService service = new();
        HardwareTestHelper.AssertCapturesZone(service, HardwareTestHelper.Center);
    }

    [TestMethod]
    public void CapturesDownscaledZone()
    {
        using DX11ScreenCaptureService service = new();
        HardwareTestHelper.AssertCapturesZone(service, HardwareTestHelper.Center, downscaleLevel: 2);
    }

    [TestMethod]
    public void TimestampIsAStopwatchTimestamp()
    {
        using DX11ScreenCaptureService service = new();
        (_, Display display) = HardwareTestHelper.GetFirstDisplay(service);
        IScreenCapture screenCapture = service.GetScreenCapture(display);

        ScreenCaptureUpdatedEventArgs args = default;
        screenCapture.Updated += (_, e) => args = e;

        HardwareTestHelper.CaptureFrame(screenCapture);
        long now = Stopwatch.GetTimestamp();

        // DDA's LastPresentTime is a QueryPerformanceCounter value: on another clock (e.g. FILETIME) it would be far in the future.
        Assert.AreEqual(CaptureResult.NewFrame, args.Result);
        Assert.IsGreaterThan(0, args.FrameNumber);
        Assert.IsInRange(1, now, args.Timestamp);
    }

    [TestMethod]
    public void NoNewFrameIsNoFailure()
    {
        using DX11ScreenCaptureService service = new();
        (_, Display display) = HardwareTestHelper.GetFirstDisplay(service);
        DX11ScreenCapture screenCapture = service.GetScreenCapture(display);
        HardwareTestHelper.CaptureFrame(screenCapture);

        // Without waiting, most calls find no new frame (unless something renders at a high rate). Neither may count as a failure.
        screenCapture.Timeout = TimeSpan.Zero;
        CaptureResult[] results = [.. Enumerable.Range(0, 50).Select(_ => screenCapture.CaptureScreen())];

        CollectionAssert.IsSubsetOf(results.Distinct().ToArray(), new[] { CaptureResult.NewFrame, CaptureResult.NoChange, CaptureResult.Timeout },
                                    $"Unexpected results: {string.Join(", ", results.Distinct())}");
    }

    /// <summary>
    /// Without a new frame, a zone registered since is written from the last frame (DX11 keeps it in the capture texture),
    /// and zones that already show it aren't written again.
    /// </summary>
    [TestMethod]
    public void ZoneAddedOnAStaticScreenGetsTheLastFrame()
    {
        using DX11ScreenCaptureService service = new();
        (_, Display display) = HardwareTestHelper.GetFirstDisplay(service);
        DX11ScreenCapture screenCapture = service.GetScreenCapture(display);

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
        using DX11ScreenCaptureService service = new();
        HardwareTestHelper.AssertMovedZoneCapturesNewPosition(service);
    }

    [TestMethod]
    public void MovedDownscaledZoneCapturesNewPosition()
    {
        using DX11ScreenCaptureService service = new();
        HardwareTestHelper.AssertMovedZoneCapturesNewPosition(service, downscaleLevel: 2);
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(2)]
    public void ZonesChangedDuringACaptureShowTheChange(int downscaleLevel)
    {
        using DX11ScreenCaptureService service = new();

        // The held capture doesn't wait for a frame: the zones are changed after it got one.
        HardwareTestHelper.AssertZonesChangedDuringACaptureShowTheChange(service, downscaleLevel, c => ((DX11ScreenCapture)c).Timeout = TimeSpan.Zero);
    }

    [TestMethod]
    public void ZoneShowsTheLatestFrame()
    {
        using DX11ScreenCaptureService service = new();
        HardwareTestHelper.AssertZoneShowsTheLatestFrame(service);
    }

    [TestMethod]
    public void ZonesCanBeChangedWhileCapturing()
    {
        using DX11ScreenCaptureService service = new();
        HardwareTestHelper.AssertZonesCanBeChangedWhileCapturing(service);
    }

    [TestMethod]
    public void DownscaledZonesCanBeChangedWhileCapturing()
    {
        using DX11ScreenCaptureService service = new();
        HardwareTestHelper.AssertZonesCanBeChangedWhileCapturing(service, downscaleLevel: 2);
    }

    [TestMethod]
    public void PartialZonesMatchFullScreen()
    {
        using DX11ScreenCaptureService service = new();
        HardwareTestHelper.AssertPartialZonesMatchFullScreen(service);
    }

    [TestMethod]
    public void MatchesDX9Orientation()
    {
        // With CaptureHdr: without it, a display showing HDR is captured far too bright (Windows' own conversion), while DX9 gets its SDR colors.
        // The orientation is handled the same way with both duplications (DuplicateOutput1 on SDR displays).
        Assert.IsTrue(DPIAwareness.Initialize());

        using DX11ScreenCaptureService service = new(new DX11CaptureOptions { CaptureHdr = true });
        using DX9ScreenCaptureService reference = new();
        HardwareTestHelper.AssertMatchesReferenceOrientation(service, reference);
    }

    [TestMethod]
    public void DisposeIsRepeatable()
    {
        using DX11ScreenCaptureService service = new();
        HardwareTestHelper.AssertDisposeIsRepeatable(service);
    }

    [TestMethod]
    public void FailedRestartLeavesNoDisposedObjects()
    {
        DXGI.CreateDXGIFactory1(out IDXGIFactory1? factory).CheckError();
        using IDXGIFactory1 _ = factory!;

        // The display isn't connected, so every restart fails.
        Display display = HardwareTestHelper.CreateMissingDisplay();
        using DX11ScreenCapture screenCapture = new(factory!, display, DX11CaptureOptions.Default);
        AssertNoDirectXObjects(screenCapture);

        // Zones can be registered while the capture isn't running; they're filled once it (re)starts.
        CaptureZone<ColorBGRA> zone = screenCapture.RegisterCaptureZone(new CaptureRegion(0, 0, 64, 64));

        Assert.AreEqual(CaptureResult.Unavailable, screenCapture.CaptureScreen());
        AssertNoDirectXObjects(screenCapture);

        Assert.IsTrue(screenCapture.UnregisterCaptureZone(zone));
    }

    [TestMethod]
    public void FailedRestartIsReported()
    {
        DXGI.CreateDXGIFactory1(out IDXGIFactory1? factory).CheckError();
        using IDXGIFactory1 _ = factory!;

        Display display = HardwareTestHelper.CreateMissingDisplay();
        using DX11ScreenCapture screenCapture = new(factory!, display, DX11CaptureOptions.Default);

        // The restart in the constructor failed already.
        Exception? constructorError = screenCapture.LastError;
        Assert.IsNotNull(constructorError);

        List<Exception> errors = [];
        screenCapture.ErrorOccurred += (_, e) => errors.Add(e.Exception);

        Assert.AreEqual(CaptureResult.Unavailable, screenCapture.CaptureScreen());

        // The constructor's error first, then the one of the restart in this capture.
        Assert.HasCount(2, errors);
        Assert.AreSame(constructorError, errors[0]);
        Assert.AreSame(screenCapture.LastError, errors[1]);
        Assert.AreEqual(constructorError.GetType(), errors[1].GetType());
    }

    [TestMethod]
    public void FailedRestartsBackOff()
    {
        DXGI.CreateDXGIFactory1(out IDXGIFactory1? factory).CheckError();
        using IDXGIFactory1 _ = factory!;

        // The restart in the constructor fails, so the captures wait 100, 200 and 400 ms before their attempts.
        Display display = HardwareTestHelper.CreateMissingDisplay();
        using DX11ScreenCapture screenCapture = new(factory!, display, DX11CaptureOptions.Default);

        Stopwatch stopwatch = Stopwatch.StartNew();
        for (int i = 0; i < 3; i++)
            Assert.AreEqual(CaptureResult.Unavailable, screenCapture.CaptureScreen());
        stopwatch.Stop();

        Assert.IsInRange(TimeSpan.FromMilliseconds(690), TimeSpan.FromSeconds(3), stopwatch.Elapsed);
    }

    [TestMethod]
    public void OutdatedDisplayIsReadAgain()
    {
        using DX11ScreenCaptureService service = new();
        Display display = HardwareTestHelper.GetAllDisplays(service).Last();

        DXGI.CreateDXGIFactory1(out IDXGIFactory1? factory).CheckError();
        using IDXGIFactory1 _ = factory!;

        // Indices that don't exist (the capture finds the display by its id), another position and size.
        Display outdated = display with
        {
            Index = 99,
            X = display.X + 7,
            Width = display.Width / 2,
            Height = display.Height / 2,
            Rotation = display.Rotation == ImageRotation.None ? ImageRotation.Rotate90 : ImageRotation.None,
            GraphicsCard = display.GraphicsCard with { Index = 99 }
        };

        HardwareTestHelper.AssertOutdatedDisplayIsReadAgain(display, outdated, d => new DX11ScreenCapture(factory!, d, DX11CaptureOptions.Default));
    }

    [TestMethod]
    public void RestartDuringACaptureWritesNoEmptyZone()
    {
        using DX11ScreenCaptureService service = new();
        List<Display> displays = [.. HardwareTestHelper.GetAllDisplays(service)];

        DXGI.CreateDXGIFactory1(out IDXGIFactory1? factory).CheckError();
        using IDXGIFactory1 _ = factory!;

        // A display that shows something new gives the first capture a new frame.
        foreach (Display display in displays)
            if (HardwareTestHelper.TryAssertRestartDuringACaptureWritesNoEmptyZone(display, display with { Width = display.Width / 2, Height = display.Height / 2 },
                                                                               d => new DX11ScreenCapture(factory!, d, DX11CaptureOptions.Default)))
                return;

        Assert.Inconclusive("No display had a new frame for the first capture.");
    }

    [TestMethod]
    public void ConcurrentCapturesWork()
    {
        // A short timeout, so the threads take turns often.
        using DX11ScreenCaptureService service = new(new DX11CaptureOptions { Timeout = TimeSpan.FromMilliseconds(10) });
        HardwareTestHelper.AssertConcurrentCapturesWork(service);
    }

    [TestMethod]
    public void ThreadSafetyContractHolds()
    {
        // A short timeout, so the capturing threads take turns often.
        // Disposed by the test while everything runs; disposing it again is harmless.
        using DX11ScreenCaptureService service = new(new DX11CaptureOptions { Timeout = TimeSpan.FromMilliseconds(10) });
        HardwareTestHelper.AssertThreadSafetyContract(service);
    }

    [TestMethod]
    public void EnumerationDuringDisposeIsSafe()
        => HardwareTestHelper.AssertEnumerationDuringDisposeIsSafe(() => new DX11ScreenCaptureService());

    [TestMethod]
    public void MissingDisplayIsReported()
    {
        DXGI.CreateDXGIFactory1(out IDXGIFactory1? factory).CheckError();
        using IDXGIFactory1 _ = factory!;

        using DX11ScreenCapture screenCapture = new(factory!, HardwareTestHelper.CreateMissingDisplay(), DX11CaptureOptions.Default);

        InvalidOperationException error = Assert.IsInstanceOfType<InvalidOperationException>(screenCapture.LastError);
        StringAssert.Contains(error.Message, "'missing' isn't connected");
    }

    /// <summary>
    /// With <see cref="DX11CaptureOptions.CaptureHdr"/>, a pattern of SDR colors keeps its values on every display: tone mapped on displays showing HDR
    /// (within 1 per channel, the 16-bit frames round), and like without the option on the others (8-bit frames of <c>DuplicateOutput1</c>).
    /// <b>[HW]</b> Inconclusive (after checking the SDR displays) if no display shows HDR: turn on HDR for a display and run it again.
    /// Also inconclusive for HDR displays if the desktop changes the colors on SDR displays (Night light, color management): turn that off.
    /// Shows a small window in the center of every display for a moment.
    /// </summary>
    [TestMethod]
    public void HdrCaptureKeepsSdrColors()
    {
        const int width = 256;
        const int height = 16;

        // DuplicateOutput1 only works in a per-monitor DPI aware (version 2) process.
        Assert.IsTrue(DPIAwareness.Initialize());

        DX11CaptureOptions plainOptions = new() { Timeout = TimeSpan.FromMilliseconds(100) };
        using DX11ScreenCaptureService plainService = new(plainOptions);
        using DX11ScreenCaptureService hdrService = new(plainOptions with { CaptureHdr = true });
        ColorBGRA[] pattern = CreateSdrPattern(width, height);

        List<(Display Display, int MaxDifference, int DifferentPixels)> hdrResults = [];
        string? colorTransform = null;
        foreach (Display display in HardwareTestHelper.GetAllDisplays(hdrService))
        {
            int x = (display.Width - width) / 2;
            int y = (display.Height - height) / 2;
            CaptureRegion region = new(x, y, width, height);
            using TestPatternWindow window = new(display.X + x, display.Y + y, width, height, pattern);

            // One after another: a process can only duplicate an output once at a time.
            bool hdr = IsShowingHdr(display);
            ColorBGRA[] hdrPixels;
            using (DX11ScreenCapture screenCapture = hdrService.GetScreenCapture(display))
                hdrPixels = CapturePattern(screenCapture, region, pattern, hdr ? 1 : 0);

            // Windows' own conversion on HDR displays.
            ColorBGRA[] plainPixels;
            using (DX11ScreenCapture screenCapture = plainService.GetScreenCapture(display))
                plainPixels = CapturePattern(screenCapture, region, pattern, 0);

            (int plainDifference, int plainPixelsDifferent) = Compare(pattern, plainPixels);
            (int hdrDifference, int hdrPixelsDifferent) = Compare(pattern, hdrPixels);
            Console.WriteLine($"{display.DeviceName} ({(hdr ? "HDR" : "SDR")}): max difference with CaptureHdr {hdrDifference} ({hdrPixelsDifferent} pixels), "
                            + $"without {plainDifference} ({plainPixelsDifferent} pixels).");

            if (hdr)
                hdrResults.Add((display, hdrDifference, hdrPixelsDifferent));
            else
            {
                CollectionAssert.AreEqual(plainPixels, hdrPixels, $"{display.DeviceName} (SDR): CaptureHdr changed the image.");
                if (plainDifference > 0)
                    colorTransform ??= $"{display.DeviceName} shows the pattern with other colors (by up to {plainDifference}) also without HDR: is Night light or color management on?";
            }
        }

        if (hdrResults.Count == 0)
            Assert.Inconclusive("No display shows HDR: only the SDR displays were checked.");
        if (colorTransform != null)
            Assert.Inconclusive($"HDR displays not checked: {colorTransform}");

        foreach ((Display display, int maxDifference, int differentPixels) in hdrResults)
            Assert.IsLessThanOrEqualTo(1, maxDifference, $"{display.DeviceName} (HDR): {differentPixels} pixels differ, by up to {maxDifference}.");
    }

    // Captures until the window shows up in a frame (the pixels match within the tolerance), at most for 3 s; returns the last pixels.
    private static ColorBGRA[] CapturePattern(DX11ScreenCapture screenCapture, CaptureRegion region, ColorBGRA[] pattern, int tolerance)
    {
        Assert.IsNull(screenCapture.LastError, $"{screenCapture.Display.DeviceName} (CaptureHdr: {screenCapture.Options.CaptureHdr}): the duplication failed: {screenCapture.LastError}");

        CaptureZone<ColorBGRA> zone = screenCapture.RegisterCaptureZone(region);
        try
        {
            ColorBGRA[] pixels;
            Stopwatch stopwatch = Stopwatch.StartNew();
            do
            {
                screenCapture.CaptureScreen();
                pixels = HardwareTestHelper.GetPixels(zone);
            } while ((Compare(pattern, pixels).MaxDifference > tolerance) && (stopwatch.Elapsed < TimeSpan.FromSeconds(3)));

            return pixels;
        }
        finally
        {
            screenCapture.UnregisterCaptureZone(zone);
        }
    }

    // Every value in every channel: ramps of gray, red, green and blue, then mixed colors.
    internal static ColorBGRA[] CreateSdrPattern(int width, int height)
    {
        ColorBGRA[] pattern = new ColorBGRA[width * height];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                byte value = (byte)x;
                pattern[(y * width) + x] = y switch
                {
                    0 => new ColorBGRA(value, value, value, 255),
                    1 => new ColorBGRA(0, 0, value, 255),
                    2 => new ColorBGRA(0, value, 0, 255),
                    3 => new ColorBGRA(value, 0, 0, 255),
                    _ => new ColorBGRA((byte)((x * (y + 3)) + (y * 31)), (byte)((x * (y + 5)) + (y * 59)), (byte)((x * (y + 7)) + (y * 97)), 255)
                };
            }

        return pattern;
    }

    internal static (int MaxDifference, int DifferentPixels) Compare(ColorBGRA[] expected, ColorBGRA[] actual)
    {
        int maxDifference = 0;
        int differentPixels = 0;
        for (int i = 0; i < expected.Length; i++)
        {
            int difference = Math.Max(Math.Abs(expected[i].R - actual[i].R), Math.Max(Math.Abs(expected[i].G - actual[i].G), Math.Abs(expected[i].B - actual[i].B)));
            maxDifference = Math.Max(maxDifference, difference);
            if (difference > 0) differentPixels++;
        }

        return (maxDifference, differentPixels);
    }

    // The color space of the output: HDR (ST.2084, BT.2020) or SDR (sRGB).
    internal static bool IsShowingHdr(Display display)
    {
        DXGI.CreateDXGIFactory1(out IDXGIFactory1? factory).CheckError();
        using IDXGIFactory1 _ = factory!;

        for (int adapterIndex = 0; factory!.EnumAdapters1(adapterIndex, out IDXGIAdapter1? adapter).Success; adapterIndex++)
            using (adapter)
                for (int outputIndex = 0; adapter.EnumOutputs(outputIndex, out IDXGIOutput? output).Success; outputIndex++)
                    using (output)
                    {
                        using IDXGIOutput6 output6 = output.QueryInterface<IDXGIOutput6>();
                        OutputDescription1 description = output6.Description1;
                        if (string.Equals(description.DeviceName, display.DeviceName, StringComparison.OrdinalIgnoreCase))
                            return description.ColorSpace == ColorSpaceType.RgbFullG2084NoneP2020;
                    }

        throw new InvalidOperationException($"No output shows {display.DeviceName}.");
    }

    [TestMethod]
    public void ServiceOptionsAreUsedForNewCaptures()
    {
        DX11CaptureOptions options = new() { Timeout = TimeSpan.FromMilliseconds(250) };
        using DX11ScreenCaptureService service = new(options);
        Assert.AreSame(options, service.Options);

        (_, Display display) = HardwareTestHelper.GetFirstDisplay(service);
        DX11ScreenCapture screenCapture = service.GetScreenCapture(display);

        Assert.AreSame(options, screenCapture.Options);
        Assert.AreEqual(TimeSpan.FromMilliseconds(250), screenCapture.Timeout);
    }

    [TestMethod]
    public void CapturesCanHaveTheirOwnOptions()
    {
        using DX11ScreenCaptureService service = new();
        Assert.AreSame(DX11CaptureOptions.Default, service.Options);

        (_, Display display) = HardwareTestHelper.GetFirstDisplay(service);
        DX11CaptureOptions options = new() { Timeout = TimeSpan.FromMilliseconds(250) };
        DX11ScreenCapture screenCapture = service.GetScreenCapture(display, options);

        Assert.AreSame(options, screenCapture.Options);
        Assert.AreEqual(TimeSpan.FromMilliseconds(250), screenCapture.Timeout);

        // Without options, the existing capture is returned with its own options.
        Assert.AreSame(screenCapture, service.GetScreenCapture(display));
    }

    [TestMethod]
    public void ExistingCaptureIsReturnedForEqualOptions()
    {
        using DX11ScreenCaptureService service = new();
        (_, Display display) = HardwareTestHelper.GetFirstDisplay(service);

        DX11ScreenCapture screenCapture = service.GetScreenCapture(display, new DX11CaptureOptions { Timeout = TimeSpan.FromMilliseconds(250) });

        Assert.AreSame(screenCapture, service.GetScreenCapture(display, new DX11CaptureOptions { Timeout = TimeSpan.FromMilliseconds(250) }));
        Assert.AreSame(screenCapture, service.GetScreenCapture(display with { Width = display.Width / 2 }, screenCapture.Options));
    }

    [TestMethod]
    public void OtherOptionsForAnExistingCaptureThrow()
    {
        using DX11ScreenCaptureService service = new();
        (_, Display display) = HardwareTestHelper.GetFirstDisplay(service);
        DX11ScreenCapture screenCapture = service.GetScreenCapture(display);

        InvalidOperationException exception = Assert.ThrowsExactly<InvalidOperationException>(
            () => service.GetScreenCapture(display, new DX11CaptureOptions { CaptureHdr = true }));
        StringAssert.Contains(exception.Message, display.Id);

        // Changing the timeout of the capture doesn't change the options it was created with.
        screenCapture.Timeout = TimeSpan.FromMilliseconds(5);
        Assert.AreSame(screenCapture, service.GetScreenCapture(display, DX11CaptureOptions.Default));
        Assert.ThrowsExactly<InvalidOperationException>(() => service.GetScreenCapture(display, new DX11CaptureOptions { Timeout = TimeSpan.FromMilliseconds(5) }));

        Assert.AreSame(screenCapture, service.GetScreenCapture(display));
        Assert.ThrowsExactly<ArgumentNullException>(() => service.GetScreenCapture(display, null!));
    }

    [TestMethod]
    public void TimeoutOfACaptureCanBeChanged()
    {
        using DX11ScreenCaptureService service = new();
        (_, Display display) = HardwareTestHelper.GetFirstDisplay(service);
        DX11ScreenCapture screenCapture = service.GetScreenCapture(display);
        Assert.AreEqual(TimeSpan.FromSeconds(1), screenCapture.Timeout);

        // Rounded up to whole milliseconds.
        screenCapture.Timeout = TimeSpan.FromMilliseconds(1.5);
        Assert.AreEqual(TimeSpan.FromMilliseconds(2), screenCapture.Timeout);

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => screenCapture.Timeout = Timeout.InfiniteTimeSpan);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => screenCapture.Timeout = DX11CaptureOptions.MaxTimeout + TimeSpan.FromTicks(1));
        Assert.AreEqual(TimeSpan.FromMilliseconds(2), screenCapture.Timeout);

        Assert.AreSame(DX11CaptureOptions.Default, screenCapture.Options);
    }

    [TestMethod]
    public void ZeroTimeoutDoesntWait()
    {
        using DX11ScreenCaptureService service = new(new DX11CaptureOptions { Timeout = TimeSpan.Zero });
        (_, Display display) = HardwareTestHelper.GetFirstDisplay(service);
        DX11ScreenCapture screenCapture = service.GetScreenCapture(display);

        // Back to back, most captures find no new frame and time out right away. With a 1 s timeout, a static screen makes every capture wait 1 s,
        // and on a changing screen each capture waits for the next frame instead of timing out.
        Stopwatch stopwatch = Stopwatch.StartNew();
        CaptureResult[] results = [.. Enumerable.Range(0, 10).Select(_ => screenCapture.CaptureScreen())];
        stopwatch.Stop();

        Assert.IsLessThan(TimeSpan.FromSeconds(1), stopwatch.Elapsed);
        Assert.Contains(CaptureResult.Timeout, results, $"Results: {string.Join(", ", results)}");
    }

    private static void AssertNoDirectXObjects(DX11ScreenCapture screenCapture)
    {
        // The factory belongs to the service.
        AssertNoDirectXFields(screenCapture, 3, "_factory");
        AssertZonePipelineReleased(screenCapture);
    }

    /// <summary>
    /// The zone pipeline of a capture (DX11, WGC) holds no DirectX objects.
    /// </summary>
    internal static void AssertZonePipelineReleased(object screenCapture)
    {
        // The zone pipeline holds the capture texture, the HDR resources and the zone textures (and borrows the device and the context).
        object zonePipeline = screenCapture.GetType().GetField("_zonePipeline", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(screenCapture)
                              ?? throw new InvalidOperationException("_zonePipeline not found.");
        AssertNoDirectXFields(zonePipeline, 3);

        Type pipelineType = zonePipeline.GetType();
        Assert.IsNull(pipelineType.GetField("_hdrFrame", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(zonePipeline), "_hdrFrame wasn't reset.");
        Assert.AreEqual(0, ((System.Collections.ICollection)pipelineType.GetField("_textures", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(zonePipeline)!).Count,
                        "Zone textures were left.");
    }

    internal static void AssertNoDirectXFields(object owner, int minimumCount, params string[] ignored)
    {
        List<FieldInfo> fields = [.. owner.GetType().GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
                                          .Where(f => typeof(CppObject).IsAssignableFrom(f.FieldType) && !ignored.Contains(f.Name))];
        Assert.IsGreaterThanOrEqualTo(minimumCount, fields.Count, $"DirectX fields of {owner.GetType().Name} not found.");

        foreach (FieldInfo field in fields)
            Assert.IsNull(field.GetValue(owner), $"{owner.GetType().Name}.{field.Name} wasn't reset.");
    }
}
