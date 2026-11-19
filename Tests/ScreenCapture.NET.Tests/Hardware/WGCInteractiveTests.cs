extern alias WGC;
using System.Runtime.Versioning;
using WGC::ScreenCapture.NET;

namespace ScreenCapture.NET.Tests.Hardware;

/// <summary>
/// WGC tests that take over the desktop (they move the mouse pointer), so they only run on request:
/// <c>dotnet test --filter TestCategory=Interactive</c>, one TFM at a time (<c>-f</c>: the test processes of both would fight over the pointer).
/// Not part of a <c>TestCategory=Hardware</c> run.
/// </summary>
[TestClass]
[TestCategory(HardwareTestHelper.INTERACTIVE_CATEGORY)]
[OSCondition(OperatingSystems.Windows)]
[SupportedOSPlatform("windows")]
public sealed class WGCInteractiveTests
{
    /// <summary>
    /// With <see cref="WGCCaptureOptions.DrawCursor"/> the pointer shows in zones under it (also downscaled ones), at its position, and disappears when it moves away,
    /// also to another display. After a restart it shows again without moving.
    /// <b>Moves the mouse pointer</b> for about three seconds per display (it's put back afterwards): don't use the mouse meanwhile.
    /// </summary>
    [TestMethod]
    public void DrawsCursorIntoZones()
    {
        using WGCScreenCaptureService service = new(new WGCCaptureOptions { DrawCursor = true, Timeout = TimeSpan.FromMilliseconds(100) });
        InteractiveTestHelper.AssertDrawsCursorOnEveryDisplay(service, service.GetScreenCapture);
    }

    /// <summary>
    /// Without <see cref="WGCCaptureOptions.DrawCursor"/> the pointer doesn't show in a zone under it (Windows would draw it by default). <b>Moves the mouse pointer</b> for about a second.
    /// </summary>
    [TestMethod]
    public void CursorIsOnlyDrawnWithTheOption()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
        {
            Assert.Inconclusive("Before Windows 10 2004 the pointer is always drawn.");
            return;
        }

        using WGCScreenCaptureService service = new(new WGCCaptureOptions { Timeout = TimeSpan.FromMilliseconds(100) });
        InteractiveTestHelper.AssertCursorIsNotDrawn(service, service.GetScreenCapture);
    }
}
