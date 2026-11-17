using System.Runtime.Versioning;

namespace ScreenCapture.NET.Tests.Hardware;

/// <summary>
/// The ids, names and positions the Windows backends read from <c>QueryDisplayConfig</c>.
/// </summary>
[TestClass]
[TestCategory(HardwareTestHelper.CATEGORY)]
[OSCondition(OperatingSystems.Windows)]
[SupportedOSPlatform("windows")]
public sealed class WindowsDeviceIdentityTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void IdsAreDevicePaths()
    {
        using DX11ScreenCaptureService service = new();
        List<Display> displays = [.. HardwareTestHelper.GetAllDisplays(service)];

        foreach (Display display in displays)
            TestContext.WriteLine($"{display.DeviceName} '{display.FriendlyName}' at {display.X},{display.Y} {display.Width}x{display.Height}: {display.Id} on {display.GraphicsCard.Id}");

        if (displays.Exists(d => d.Id == d.DeviceName))
            Assert.Inconclusive("Windows has no device path for some displays (e.g. in a remote session).");

        foreach (Display display in displays)
        {
            StringAssert.StartsWith(display.Id, @"\\?\DISPLAY#", StringComparison.Ordinal);
            StringAssert.StartsWith(display.GraphicsCard.Id, @"\\?\", StringComparison.Ordinal);
        }

        if (!displays.Exists(d => d.FriendlyName.Length > 0))
            Assert.Inconclusive("No monitor reports a name.");
    }

    [TestMethod]
    public void DX9MatchesDX11()
    {
        using DX11ScreenCaptureService dx11 = new();
        using DX9ScreenCaptureService dx9 = new();

        Dictionary<string, Display> dx11Displays = HardwareTestHelper.GetAllDisplays(dx11).ToDictionary(d => d.Id, StringComparer.Ordinal);
        List<Display> dx9Displays = [.. HardwareTestHelper.GetAllDisplays(dx9)];
        Assert.IsNotEmpty(dx9Displays, "No display found.");

        foreach (Display display in dx9Displays)
        {
            Assert.IsTrue(dx11Displays.TryGetValue(display.Id, out Display reference), $"{display.Id}: missing in DX11.");

            Assert.AreEqual(reference.DeviceName, display.DeviceName);
            Assert.AreEqual(reference.FriendlyName, display.FriendlyName);
            Assert.AreEqual((reference.X, reference.Y, reference.Width, reference.Height), (display.X, display.Y, display.Width, display.Height), display.DeviceName);
            Assert.AreEqual(reference.GraphicsCard.Id, display.GraphicsCard.Id, display.DeviceName);
            Assert.AreEqual(reference.GraphicsCard.Name, display.GraphicsCard.Name, display.DeviceName);
        }
    }

    [TestMethod]
    public void DisplaysFormTheDesktop()
    {
        using DX11ScreenCaptureService service = new();
        List<Display> displays = [.. HardwareTestHelper.GetAllDisplays(service)];

        Assert.IsTrue(displays.Exists(d => (d.X == 0) && (d.Y == 0)), "No display is at 0,0 (the primary display).");

        for (int i = 0; i < displays.Count; i++)
            for (int j = i + 1; j < displays.Count; j++)
            {
                Display a = displays[i];
                Display b = displays[j];
                bool overlap = (a.X < (b.X + b.Width)) && (b.X < (a.X + a.Width)) && (a.Y < (b.Y + b.Height)) && (b.Y < (a.Y + a.Height));
                Assert.IsFalse(overlap, $"{a.DeviceName} and {b.DeviceName} overlap.");
            }
    }
}
