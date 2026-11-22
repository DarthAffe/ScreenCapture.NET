// ReSharper disable InconsistentNaming

using System;
using System.Runtime.InteropServices;


namespace ScreenCapture.NET;

/// <summary>
/// Helper-class for DPI-related WIN-API calls.
/// </summary>
public static partial class DPIAwareness
{
    #region Constants

    // A pseudo-handle (windef.h).
    private const nint DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = -4;

    #endregion

    #region Methods

    /// <summary>
    /// Makes the process per-monitor DPI aware (v2). Needed for <see cref="DX11CaptureOptions.CaptureHdr"/>.
    /// </summary>
    /// <remarks>
    /// Can only be set once, before any window is created. If the manifest, WPF or WinForms already set it, it can't be changed.
    /// </remarks>
    /// <returns><c>true</c> if the process is per-monitor DPI aware (v2) afterwards; otherwise, <c>false</c>.</returns>
    public static bool Initialize()
    {
        try
        {
            if (SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2))
                return true;

            // Fails if it was already set, which is fine if it's the right one.
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