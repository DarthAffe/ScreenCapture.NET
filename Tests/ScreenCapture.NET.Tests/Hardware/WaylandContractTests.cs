extern alias Wayland;
using System.Runtime.Versioning;
using Wayland::ScreenCapture.NET;

namespace ScreenCapture.NET.Tests.Hardware;

/// <summary>
/// The shared contract of a backend (see <see cref="HardwareTestHelper"/>), for the Wayland one: a <see cref="FakeScreenCastPortal"/> shares a <see cref="PipeWireVideoSource"/>
/// that sends frames all the time, like a monitor that changes. Needs PipeWire with a session manager (WirePlumber); inconclusive in a process that also runs the Interactive tests.
/// </summary>
[TestClass]
[TestCategory(HardwareTestHelper.CATEGORY)]
[OSCondition(OperatingSystems.Linux)]
[SupportedOSPlatform("linux")]
public sealed class WaylandContractTests
{
    private const int WIDTH = 640;
    private const int HEIGHT = 480;

    private PipeWireVideoSource? _source;
    private FakeScreenCastPortal? _portal;

    [TestInitialize]
    public void StartSource()
    {
        _source = PipeWireVideoSource.Start(WIDTH, HEIGHT);
        _source.StartPump();

        _portal = FakeScreenCastPortal.Start();
        _portal.Streams.Clear();
        _portal.Streams.Add(new FakeStream(_source.NodeId, Size: (WIDTH, HEIGHT)));
    }

    [TestCleanup]
    public void StopSource()
    {
        _portal?.Dispose();
        _source?.Dispose();
    }

    private static WaylandScreenCaptureService CreateService() => new(new WaylandCaptureOptions { Timeout = TimeSpan.FromMilliseconds(200) });

    [TestMethod]
    public void EnumeratesDisplays()
    {
        using WaylandScreenCaptureService service = CreateService();
        HardwareTestHelper.AssertEnumeratesDisplays(service);
    }

    [TestMethod]
    public void DevicesHaveStableIds()
    {
        using WaylandScreenCaptureService service = CreateService();
        HardwareTestHelper.AssertDevicesHaveStableIds(service);
    }

    [TestMethod]
    public void DisplaysAreFoundById()
    {
        using WaylandScreenCaptureService service = CreateService();
        HardwareTestHelper.AssertDisplaysAreFoundById(service);
    }

    [TestMethod]
    public void DevicesAreLookedUpById()
    {
        using WaylandScreenCaptureService service = CreateService();
        HardwareTestHelper.AssertDevicesAreLookedUpById(service);
    }

    [TestMethod]
    public void DisposedCaptureIsReplaced()
    {
        using WaylandScreenCaptureService service = CreateService();
        HardwareTestHelper.AssertDisposedCaptureIsReplaced(service);
    }

    [TestMethod]
    public void ConcurrentRequestsGetTheSameCapture()
    {
        using WaylandScreenCaptureService service = CreateService();
        HardwareTestHelper.AssertConcurrentRequestsGetTheSameCapture(service);
    }

    [TestMethod]
    public void CapturesAreCachedById()
    {
        using WaylandScreenCaptureService service = CreateService();
        HardwareTestHelper.AssertCapturesAreCachedById(service);
    }

    [TestMethod]
    public void CapturesFullScreenZone()
    {
        using WaylandScreenCaptureService service = CreateService();
        HardwareTestHelper.AssertCapturesZone(service, HardwareTestHelper.FullScreen);
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(2)]
    public void CapturesCenterZone(int downscaleLevel)
    {
        using WaylandScreenCaptureService service = CreateService();
        HardwareTestHelper.AssertCapturesZone(service, HardwareTestHelper.Center, downscaleLevel);
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(2)]
    public void MovedZoneCapturesNewPosition(int downscaleLevel)
    {
        using WaylandScreenCaptureService service = CreateService();
        HardwareTestHelper.AssertMovedZoneCapturesNewPosition(service, downscaleLevel);
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(2)]
    public void ZonesChangedDuringACaptureShowTheChange(int downscaleLevel)
    {
        using WaylandScreenCaptureService service = CreateService();
        HardwareTestHelper.AssertZonesChangedDuringACaptureShowTheChange(service, downscaleLevel);
    }

    [TestMethod]
    public void ZoneShowsTheLatestFrame()
    {
        using WaylandScreenCaptureService service = CreateService();
        HardwareTestHelper.AssertZoneShowsTheLatestFrame(service);
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(2)]
    public void ZonesCanBeChangedWhileCapturing(int downscaleLevel)
    {
        using WaylandScreenCaptureService service = CreateService();
        HardwareTestHelper.AssertZonesCanBeChangedWhileCapturing(service, downscaleLevel);
    }

    [TestMethod]
    public void ConcurrentCapturesWork()
    {
        using WaylandScreenCaptureService service = CreateService();
        HardwareTestHelper.AssertConcurrentCapturesWork(service);
    }

    [TestMethod]
    public void ThreadSafetyContractHolds()
    {
        // Disposed by the test while everything runs; disposing it again is harmless.
        using WaylandScreenCaptureService service = CreateService();
        HardwareTestHelper.AssertThreadSafetyContract(service);
    }

    [TestMethod]
    public void EnumerationDuringDisposeIsSafe() => HardwareTestHelper.AssertEnumerationDuringDisposeIsSafe(CreateService);

    [TestMethod]
    public void PartialZonesMatchFullScreen()
    {
        using WaylandScreenCaptureService service = CreateService();
        HardwareTestHelper.AssertPartialZonesMatchFullScreen(service);
    }

    [TestMethod]
    public void DisposeIsRepeatable()
    {
        using WaylandScreenCaptureService service = CreateService();
        HardwareTestHelper.AssertDisposeIsRepeatable(service);
    }
}
