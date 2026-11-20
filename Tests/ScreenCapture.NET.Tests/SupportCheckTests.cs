extern alias WGC;
extern alias Wayland;

namespace ScreenCapture.NET.Tests;

/// <summary>
/// An application can call the support checks of all backends on every system: the ones of the other platform are <c>false</c> and don't throw.
/// </summary>
[TestClass]
public sealed class SupportCheckTests
{
    [TestMethod]
    [OSCondition(OperatingSystems.Linux)]
    public void WindowsBackendsAreNotSupportedOnLinux()
    {
        Assert.IsFalse(DX11ScreenCaptureService.IsSupported());
        Assert.IsFalse(DX9ScreenCaptureService.IsSupported());
        Assert.IsFalse(WGC::ScreenCapture.NET.WGCScreenCaptureService.IsSupported());
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Windows)]
    public void LinuxBackendsAreNotSupportedOnWindows()
    {
        Assert.IsFalse(X11ScreenCaptureService.IsSupported());
        Assert.IsFalse(Wayland::ScreenCapture.NET.WaylandScreenCaptureService.IsSupported());
    }
}
