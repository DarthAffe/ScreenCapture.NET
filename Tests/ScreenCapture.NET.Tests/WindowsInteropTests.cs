using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace ScreenCapture.NET.Tests;

/// <summary>
/// Pins the layout of the Windows interop types to the Windows SDK. These are pure managed checks, so they run everywhere.
/// </summary>
[TestClass]
[SupportedOSPlatform("windows")] // The Windows assemblies are marked windows-only, but only their types are inspected.
public sealed class WindowsInteropTests
{
    // The DX11 and the DX9 assembly each compile their own copy of the shared helper.
    public static IEnumerable<object[]> Assemblies => [[typeof(DX11ScreenCaptureService).Assembly], [typeof(DX9ScreenCaptureService).Assembly]];

    [TestMethod]
    [DynamicData(nameof(Assemblies))]
    public void DisplayConfigTypesMatchWingdi(Assembly assembly)
    {
        Type helper = assembly.GetType("ScreenCapture.NET.WindowsDisplayDevices", throwOnError: true)!;

        AssertSize(helper, "PathInfo", 72);         // DISPLAYCONFIG_PATH_INFO
        AssertSize(helper, "ModeInfo", 64);         // DISPLAYCONFIG_MODE_INFO
        AssertSize(helper, "DeviceInfoHeader", 20); // DISPLAYCONFIG_DEVICE_INFO_HEADER
        AssertSize(helper, "SourceDeviceName", 84); // DISPLAYCONFIG_SOURCE_DEVICE_NAME
        AssertSize(helper, "TargetDeviceName", 420); // DISPLAYCONFIG_TARGET_DEVICE_NAME
        AssertSize(helper, "AdapterName", 276);     // DISPLAYCONFIG_ADAPTER_NAME

        Type targetName = GetNestedType(helper, "TargetDeviceName");
        Assert.AreEqual(36, Marshal.OffsetOf(targetName, "MonitorFriendlyDeviceName").ToInt32());
        Assert.AreEqual(164, Marshal.OffsetOf(targetName, "MonitorDevicePath").ToInt32());

        Type pathInfo = GetNestedType(helper, "PathInfo");
        Assert.AreEqual(20, Marshal.OffsetOf(pathInfo, "TargetAdapterId").ToInt32());
        Assert.AreEqual(28, Marshal.OffsetOf(pathInfo, "TargetId").ToInt32());
    }

    [TestMethod]
    public void MonitorInfoMatchesWinuser()
        => AssertSize(typeof(DX9ScreenCaptureService), "MonitorInfo", 40);

    private static void AssertSize(Type owner, string name, int size)
        => Assert.AreEqual(size, Marshal.SizeOf(GetNestedType(owner, name)), name);

    private static Type GetNestedType(Type owner, string name)
        => owner.GetNestedType(name, BindingFlags.NonPublic) ?? throw new InvalidOperationException($"{owner.Name}.{name} not found.");
}
