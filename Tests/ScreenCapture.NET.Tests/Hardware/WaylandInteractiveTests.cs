extern alias Wayland;
using System.Diagnostics;
using System.Runtime.Versioning;
using HPPH;
using Wayland::ScreenCapture.NET;

namespace ScreenCapture.NET.Tests.Hardware;

/// <summary>
/// Wayland tests with the real ScreenCast portal: the compositor shows its share dialog, which the user has to answer (share at least one monitor).
/// They only run on request: <c>dotnet test --filter TestCategory=Interactive</c>, one TFM at a time (<c>-f</c>), in a Wayland session.
/// Not part of a <c>TestCategory=Hardware</c> run.
/// </summary>
[TestClass]
[TestCategory(HardwareTestHelper.INTERACTIVE_CATEGORY)]
[OSCondition(OperatingSystems.Linux)]
[SupportedOSPlatform("linux")]
public sealed class WaylandInteractiveTests
{
    /// <summary>
    /// <b>Shows the share dialog once</b>: share one or more monitors. Later calls return the same displays without a dialog.
    /// </summary>
    [TestMethod]
    public void DisplaysAreTheSharedMonitors()
    {
        if (Environment.GetEnvironmentVariable("WAYLAND_DISPLAY") == null)
            Assert.Inconclusive("Not in a Wayland session.");

        using WaylandScreenCaptureService service = new();
        GraphicsCard graphicsCard = service.GetGraphicsCards().Single();

        List<Display> displays = [.. service.GetDisplays(graphicsCard)];
        Assert.IsNotEmpty(displays);
        foreach (Display display in displays)
            Assert.IsTrue((display.Width > 0) && (display.Height > 0), $"{display.Id} has no size.");

        Assert.AreEqual(displays.Count, displays.Select(display => display.Id).Distinct().Count(), "The ids aren't unique.");

        Stopwatch stopwatch = Stopwatch.StartNew();
        CollectionAssert.AreEqual(displays, service.GetDisplays(graphicsCard).ToList());
        Assert.IsLessThan(100, stopwatch.ElapsedMilliseconds, "The second call showed a dialog.");
    }

    /// <summary>
    /// <b>Shows the share dialog once per row</b>: share one or more monitors. Each one is captured (the compositor sends a frame when the stream starts),
    /// also with the pointer drawn into the frames.
    /// </summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void SharedMonitorsAreCaptured(bool drawCursor)
    {
        if (Environment.GetEnvironmentVariable("WAYLAND_DISPLAY") == null)
            Assert.Inconclusive("Not in a Wayland session.");

        using WaylandScreenCaptureService service = new(sessionOptions: new WaylandSessionOptions { DrawCursor = drawCursor });
        foreach (Display display in service.GetDisplays(service.GetGraphicsCards().Single()).ToList())
        {
            WaylandScreenCapture capture = service.GetScreenCapture(display);
            CaptureZone<ColorBGRA> zone = capture.RegisterCaptureZone(new CaptureRegion(0, 0, display.Width, display.Height), downscaleLevel: 3);
            HardwareTestHelper.CaptureFrame(capture);

            Assert.AreEqual((display.Width, display.Height), (capture.Display.Width, capture.Display.Height), $"The frames of {display.Id} have another size than the portal told.");

            ColorBGRA[] pixels = HardwareTestHelper.GetPixels(zone);
            Assert.IsTrue(pixels.Any(pixel => (pixel.R | pixel.G | pixel.B) != 0), $"{display.Id} is black.");
            Assert.IsTrue(pixels.All(pixel => pixel.A == byte.MaxValue), $"{display.Id} isn't opaque.");
        }
    }
}
