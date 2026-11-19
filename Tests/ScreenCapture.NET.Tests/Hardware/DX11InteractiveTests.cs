using System.Runtime.Versioning;

namespace ScreenCapture.NET.Tests.Hardware;

/// <summary>
/// DX11 tests that take over the desktop (they move the mouse pointer), so they only run on request:
/// <c>dotnet test --filter TestCategory=Interactive</c>, one TFM at a time (<c>-f</c>: the test processes of both would fight over the pointer).
/// Not part of a <c>TestCategory=Hardware</c> run.
/// </summary>
[TestClass]
[TestCategory(HardwareTestHelper.INTERACTIVE_CATEGORY)]
[OSCondition(OperatingSystems.Windows)]
[SupportedOSPlatform("windows")]
public sealed class DX11InteractiveTests
{
    /// <summary>
    /// With <see cref="DX11CaptureOptions.DrawCursor"/> the pointer shows in zones under it (also downscaled ones), at its position, and disappears when it moves away,
    /// also to another display. After a restart it shows again without moving.
    /// Checked on every display, so rotated ones show if the position and the shape are in the orientation of the display.
    /// <b>Moves the mouse pointer</b> for about three seconds per display (it's put back afterwards): don't use the mouse meanwhile.
    /// </summary>
    [TestMethod]
    public void DrawsCursorIntoZones()
    {
        using DX11ScreenCaptureService service = new(new DX11CaptureOptions { DrawCursor = true, Timeout = TimeSpan.FromMilliseconds(100) });
        InteractiveTestHelper.AssertDrawsCursorOnEveryDisplay(service, service.GetScreenCapture);
    }

    /// <summary>
    /// Without <see cref="DX11CaptureOptions.DrawCursor"/> the pointer doesn't show in a zone under it. <b>Moves the mouse pointer</b> for about a second.
    /// </summary>
    [TestMethod]
    public void CursorIsOnlyDrawnWithTheOption()
    {
        using DX11ScreenCaptureService service = new(new DX11CaptureOptions { Timeout = TimeSpan.FromMilliseconds(100) });
        InteractiveTestHelper.AssertCursorIsNotDrawn(service, service.GetScreenCapture);
    }
}
