using System.Diagnostics;
using System.Runtime.Versioning;

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
}
