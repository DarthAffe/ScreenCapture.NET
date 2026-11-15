// ReSharper disable InconsistentNaming

using System;
using System.Runtime.InteropServices;

// user32.dll is a system library; never load it from anywhere else.
[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]

namespace ScreenCapture.NET;

/// <summary>
/// Helper-class for DPI-related WIN-API calls.
/// </summary>
public static partial class DPIAwareness
{
    #region Constants

    // DPI_AWARENESS_CONTEXT is a handle. The predefined contexts are the pseudo-handles -1 to -5 (windef.h).
    private const nint DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = -4;

    #endregion

    #region Methods

    /// <summary>
    /// Makes the process per-monitor DPI aware (version 2). This is needed when using desktop duplication with <c>DuplicateOutput1</c>.
    /// </summary>
    /// <remarks>
    /// The DPI awareness of a process can only be set once, before any window is created.
    /// If it was already set (e.g. by the application manifest, WPF or WinForms), it can't be changed anymore.
    /// </remarks>
    /// <returns><c>true</c> if the process is per-monitor DPI aware (version 2) afterwards (also if it already was); otherwise, <c>false</c>.</returns>
    public static bool Initialize()
    {
        try
        {
            if (SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2))
                return true;

            // Fails with ERROR_ACCESS_DENIED if the awareness was already set. That's fine if it's the one we want.
            return AreDpiAwarenessContextsEqual(GetDpiAwarenessContextForProcess(0), DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        }
        catch (EntryPointNotFoundException)
        {
            // Windows 10 before 1803.
            return false;
        }
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetProcessDpiAwarenessContext(nint value);

    [LibraryImport("user32.dll")]
    private static partial nint GetDpiAwarenessContextForProcess(nint hProcess);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AreDpiAwarenessContextsEqual(nint dpiContextA, nint dpiContextB);

    #endregion
}