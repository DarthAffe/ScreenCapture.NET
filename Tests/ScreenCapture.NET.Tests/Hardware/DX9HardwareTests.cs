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
}
