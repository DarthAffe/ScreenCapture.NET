using System.Runtime.Versioning;

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
