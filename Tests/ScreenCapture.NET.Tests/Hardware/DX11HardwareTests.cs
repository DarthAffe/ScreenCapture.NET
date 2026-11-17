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
        screenCapture.Timeout = 0;
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
        screenCapture.Timeout = 0;

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
        using DX11ScreenCaptureService service = new();
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

        // The output doesn't exist, so every restart fails after the device was created.
        Display display = HardwareTestHelper.CreateMissingDisplay();
        using DX11ScreenCapture screenCapture = new(factory!, display);
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
        using DX11ScreenCapture screenCapture = new(factory!, display);

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
        using DX11ScreenCapture screenCapture = new(factory!, display);

        Stopwatch stopwatch = Stopwatch.StartNew();
        for (int i = 0; i < 3; i++)
            Assert.AreEqual(CaptureResult.Unavailable, screenCapture.CaptureScreen());
        stopwatch.Stop();

        Assert.IsInRange(TimeSpan.FromMilliseconds(690), TimeSpan.FromSeconds(3), stopwatch.Elapsed);
    }

    [TestMethod]
    public void CapturesWithDuplicateOutput1()
    {
        // DuplicateOutput1 only works in a per-monitor DPI aware (version 2) process.
        Assert.IsTrue(DPIAwareness.Initialize());

        Display display;
        using (DX11ScreenCaptureService service = new())
            display = HardwareTestHelper.GetFirstDisplay(service).display;

        DXGI.CreateDXGIFactory1(out IDXGIFactory1? factory).CheckError();
        using IDXGIFactory1 _ = factory!;

        using DX11ScreenCapture screenCapture = new(factory!, display, useNewDuplicationAdapter: true);
        CaptureZone<ColorBGRA> zone = screenCapture.RegisterCaptureZone(new CaptureRegion(0, 0, display.Width, display.Height));

        HardwareTestHelper.CaptureFrame(screenCapture);

        using CaptureFrame<ColorBGRA> frame = zone.Lock();
        Assert.IsTrue(frame.RawBuffer.ContainsAnyExcept((byte)0), "Captured zone contains only zeros.");
    }

    private static void AssertNoDirectXObjects(DX11ScreenCapture screenCapture)
    {
        // The factory belongs to the service.
        List<FieldInfo> fields = [.. typeof(DX11ScreenCapture).GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
                                                              .Where(f => typeof(CppObject).IsAssignableFrom(f.FieldType) && (f.Name != "_factory"))];
        Assert.IsGreaterThanOrEqualTo(4, fields.Count, "DirectX fields not found.");

        foreach (FieldInfo field in fields)
            Assert.IsNull(field.GetValue(screenCapture), $"{field.Name} wasn't reset.");
    }
}
