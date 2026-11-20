using System.Runtime.Versioning;

namespace ScreenCapture.NET.Tests;

/// <summary>
/// Checks the parts of the Wayland backend that need no session bus.
/// </summary>
[TestClass]
[SupportedOSPlatform("linux")] // The Wayland assembly is marked linux-only, but these members are pure managed code that also runs on Windows.
public sealed class WaylandInteropTests
{
    [TestMethod]
    [DataRow(":1.42", "/org/freedesktop/portal/desktop/request/1_42/token_1")]
    [DataRow(":1.1234.5", "/org/freedesktop/portal/desktop/request/1_1234_5/token_1")]
    public void RequestPathIsUnderTheEscapedUniqueName(string uniqueName, string path)
        => Assert.AreEqual(path, ScreenCastSession.GetRequestPath(uniqueName, "token_1"));

    [TestMethod]
    public void CancelledResponseSaysSo()
    {
        Assert.Contains("cancelled", ScreenCastSession.CreateResponseException("Start", 1).Message);
        Assert.DoesNotContain("cancelled", ScreenCastSession.CreateResponseException("Start", 2).Message);
    }

    [TestMethod]
    [DataRow("KDE", "KDE")]
    [DataRow("ubuntu:GNOME", "ubuntu:GNOME")]
    [DataRow("", "xdg-desktop-portal")]
    [DataRow(null, "xdg-desktop-portal")]
    public void GraphicsCardIsNamedAfterTheDesktop(string? desktop, string name)
    {
        string? previous = Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP");
        try
        {
            Environment.SetEnvironmentVariable("XDG_CURRENT_DESKTOP", desktop);

            Assert.AreEqual(new GraphicsCard("xdg-desktop-portal", 0, name, 0, 0), WaylandDisplays.GetGraphicsCard());
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_CURRENT_DESKTOP", previous);
        }
    }
}
