using System.Runtime.Versioning;
using Vortice.Direct3D9;

namespace ScreenCapture.NET.Tests.Hardware;

[TestClass]
[TestCategory(HardwareTestHelper.CATEGORY)]
[OSCondition(OperatingSystems.Windows)]
[SupportedOSPlatform("windows")]
public sealed class DX9HardwareTests
{
    [TestMethod]
    public void EnumeratesDisplays()
    {
        using DX9ScreenCaptureService service = new();
        HardwareTestHelper.AssertEnumeratesDisplays(service);
    }

    [TestMethod]
    public void DevicesHaveStableIds()
    {
        using DX9ScreenCaptureService service = new();
        HardwareTestHelper.AssertDevicesHaveStableIds(service);
    }

    [TestMethod]
    public void OptionsAreUsedForCaptures()
    {
        DX9CaptureOptions options = new();
        using DX9ScreenCaptureService service = new(options);
        Assert.AreSame(options, service.Options);

        (_, Display display) = HardwareTestHelper.GetFirstDisplay(service);
        DX9ScreenCapture screenCapture = service.GetScreenCapture(display);
        Assert.AreSame(options, screenCapture.Options);

        // Equal options return the existing capture.
        Assert.AreSame(screenCapture, service.GetScreenCapture(display, new DX9CaptureOptions()));
        Assert.ThrowsExactly<ArgumentNullException>(() => service.GetScreenCapture(display, null!));

        using DX9ScreenCaptureService defaultService = new();
        Assert.AreSame(DX9CaptureOptions.Default, defaultService.Options);
    }

    [TestMethod]
    public void OutdatedDisplayIsReadAgain()
    {
        using DX9ScreenCaptureService service = new();
        Display display = HardwareTestHelper.GetAllDisplays(service).Last();

        D3D9.Direct3DCreate9Ex(out IDirect3D9Ex direct3D9).CheckError();
        using IDirect3D9Ex _ = direct3D9;

        // An adapter index that doesn't exist (the capture finds the display by its id), another position and size.
        Display outdated = display with
        {
            Index = 99,
            Y = display.Y - 7,
            Width = display.Width / 2,
            Height = display.Height / 2,
            GraphicsCard = display.GraphicsCard with { Index = 99 }
        };

        HardwareTestHelper.AssertOutdatedDisplayIsReadAgain(display, outdated, d => new DX9ScreenCapture(direct3D9, d, DX9CaptureOptions.Default));
    }

    [TestMethod]
    public void RestartDuringACaptureWritesNoEmptyZone()
    {
        using DX9ScreenCaptureService service = new();
        List<Display> displays = [.. HardwareTestHelper.GetAllDisplays(service)];

        D3D9.Direct3DCreate9Ex(out IDirect3D9Ex direct3D9).CheckError();
        using IDirect3D9Ex _ = direct3D9;

        // A display that shows something new gives the first capture a new frame.
        foreach (Display display in displays)
            if (HardwareTestHelper.TryAssertRestartDuringACaptureWritesNoEmptyZone(display, display with { Width = display.Width / 2, Height = display.Height / 2 },
                                                                               d => new DX9ScreenCapture(direct3D9, d, DX9CaptureOptions.Default)))
                return;

        Assert.Inconclusive("No display had a new frame for the first capture.");
    }

    [TestMethod]
    public void ConcurrentCapturesWork()
    {
        using DX9ScreenCaptureService service = new();
        HardwareTestHelper.AssertConcurrentCapturesWork(service);
    }

    [TestMethod]
    public void ThreadSafetyContractHolds()
    {
        // Disposed by the test while everything runs; disposing it again is harmless.
        using DX9ScreenCaptureService service = new();
        HardwareTestHelper.AssertThreadSafetyContract(service);
    }

    [TestMethod]
    public void EnumerationDuringDisposeIsSafe()
        => HardwareTestHelper.AssertEnumerationDuringDisposeIsSafe(() => new DX9ScreenCaptureService());

    [TestMethod]
    public void MissingDisplayIsReported()
    {
        D3D9.Direct3DCreate9Ex(out IDirect3D9Ex direct3D9).CheckError();
        using IDirect3D9Ex _ = direct3D9;

        using DX9ScreenCapture screenCapture = new(direct3D9, HardwareTestHelper.CreateMissingDisplay(), DX9CaptureOptions.Default);

        InvalidOperationException error = Assert.IsInstanceOfType<InvalidOperationException>(screenCapture.LastError);
        StringAssert.Contains(error.Message, "'missing' isn't connected");
        Assert.AreEqual(CaptureResult.Unavailable, screenCapture.CaptureScreen());
    }

    [TestMethod]
    public void DisplaysAreFoundById()
    {
        using DX9ScreenCaptureService service = new();
        HardwareTestHelper.AssertDisplaysAreFoundById(service);
    }

    [TestMethod]
    public void DevicesAreLookedUpById()
    {
        using DX9ScreenCaptureService service = new();
        HardwareTestHelper.AssertDevicesAreLookedUpById(service);
    }

    [TestMethod]
    public void DisposedCaptureIsReplaced()
    {
        using DX9ScreenCaptureService service = new();
        HardwareTestHelper.AssertDisposedCaptureIsReplaced(service);
    }

    [TestMethod]
    public void ConcurrentRequestsGetTheSameCapture()
    {
        using DX9ScreenCaptureService service = new();
        HardwareTestHelper.AssertConcurrentRequestsGetTheSameCapture(service);
    }

    [TestMethod]
    public void CapturesAreCachedById()
    {
        using DX9ScreenCaptureService service = new();
        HardwareTestHelper.AssertCapturesAreCachedById(service);
    }

    [TestMethod]
    public void CapturesFullScreenZone()
    {
        using DX9ScreenCaptureService service = new();
        HardwareTestHelper.AssertCapturesZone(service, HardwareTestHelper.FullScreen);
    }

    [TestMethod]
    public void CapturesPartialZone()
    {
        using DX9ScreenCaptureService service = new();
        HardwareTestHelper.AssertCapturesZone(service, HardwareTestHelper.Center);
    }

    [TestMethod]
    public void CapturesDownscaledZone()
    {
        using DX9ScreenCaptureService service = new();
        HardwareTestHelper.AssertCapturesZone(service, HardwareTestHelper.Center, downscaleLevel: 2);
    }

    [TestMethod]
    public void MovedZoneCapturesNewPosition()
    {
        using DX9ScreenCaptureService service = new();
        HardwareTestHelper.AssertMovedZoneCapturesNewPosition(service);
    }

    [TestMethod]
    public void MovedDownscaledZoneCapturesNewPosition()
    {
        using DX9ScreenCaptureService service = new();
        HardwareTestHelper.AssertMovedZoneCapturesNewPosition(service, downscaleLevel: 2);
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(2)]
    public void ZonesChangedDuringACaptureShowTheChange(int downscaleLevel)
    {
        using DX9ScreenCaptureService service = new();
        HardwareTestHelper.AssertZonesChangedDuringACaptureShowTheChange(service, downscaleLevel);
    }

    [TestMethod]
    public void ZoneShowsTheLatestFrame()
    {
        using DX9ScreenCaptureService service = new();
        HardwareTestHelper.AssertZoneShowsTheLatestFrame(service);
    }

    [TestMethod]
    public void ZonesCanBeChangedWhileCapturing()
    {
        using DX9ScreenCaptureService service = new();
        HardwareTestHelper.AssertZonesCanBeChangedWhileCapturing(service);
    }

    [TestMethod]
    public void DownscaledZonesCanBeChangedWhileCapturing()
    {
        using DX9ScreenCaptureService service = new();
        HardwareTestHelper.AssertZonesCanBeChangedWhileCapturing(service, downscaleLevel: 2);
    }

    [TestMethod]
    public void PartialZonesMatchFullScreen()
    {
        using DX9ScreenCaptureService service = new();
        HardwareTestHelper.AssertPartialZonesMatchFullScreen(service);
    }

    [TestMethod]
    public void DisposeIsRepeatable()
    {
        using DX9ScreenCaptureService service = new();
        HardwareTestHelper.AssertDisposeIsRepeatable(service);
    }
}
