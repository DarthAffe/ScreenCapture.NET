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
        Display display = new(99, "missing", 640, 480, Rotation.None, new GraphicsCard(0, "first", 0, 0));
        using DX11ScreenCapture screenCapture = new(factory!, display);
        AssertNoDirectXObjects(screenCapture);

        // Zones can be registered while the capture isn't running; they're filled once it (re)starts.
        CaptureZone<ColorBGRA> zone = screenCapture.RegisterCaptureZone(0, 0, 64, 64);

        Assert.IsFalse(screenCapture.CaptureScreen());
        AssertNoDirectXObjects(screenCapture);

        Assert.IsTrue(screenCapture.UnregisterCaptureZone(zone));
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
