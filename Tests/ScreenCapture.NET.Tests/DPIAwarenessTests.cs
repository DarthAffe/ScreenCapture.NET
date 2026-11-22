using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace ScreenCapture.NET.Tests;

/// <summary>
/// Checks <see cref="DPIAwareness"/>. This makes the test process per-monitor DPI aware (version 2), which can't be undone.
/// </summary>
[TestClass]
[OSCondition(OperatingSystems.Windows)]
[SupportedOSPlatform("windows")]
public sealed partial class DPIAwarenessTests
{
    private const nint DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = -4;

    [TestMethod]
    public void InitializeMakesProcessPerMonitorV2Aware()
    {
        Assert.IsTrue(DPIAwareness.Initialize());
        Assert.IsTrue(AreDpiAwarenessContextsEqual(GetDpiAwarenessContextForProcess(0), DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2));

        // Fails in Windows since it's already set, but to the requested awareness.
        Assert.IsTrue(DPIAwareness.Initialize());
    }

    [LibraryImport("user32.dll")]
    private static partial nint GetDpiAwarenessContextForProcess(nint hProcess);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AreDpiAwarenessContextsEqual(nint dpiContextA, nint dpiContextB);
}
