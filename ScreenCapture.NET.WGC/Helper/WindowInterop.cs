using System;
using System.Globalization;
using System.Runtime.InteropServices;
using HPPH;

namespace ScreenCapture.NET;

/// <summary>
/// The Win32 calls about the windows captured by <see cref="WGCScreenCapture"/>, and the <see cref="Display"/> describing such a window.
/// </summary>
internal static partial class WindowInterop
{
    #region Constants

    // Ids of window captures: this prefix and the handle in hex (e.g. "window:0x1A2B3C").
    private const string WINDOW_ID_PREFIX = "window:0x";

    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private const uint GA_ROOT = 2;
    private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;

    #endregion

    #region Methods

    /// <summary>
    /// Gets the <see cref="Display.Id"/> of the capture of a window.
    /// </summary>
    public static string GetWindowId(nint window) => WINDOW_ID_PREFIX + ((long)window).ToString("X", CultureInfo.InvariantCulture);

    /// <summary>
    /// Gets the window of a <see cref="Display.Id"/> created by <see cref="GetWindowId"/>.
    /// </summary>
    /// <returns><c>true</c> if the id is the one of a window; otherwise (e.g. the one of a display), <c>false</c>.</returns>
    public static bool TryGetWindow(string id, out nint window)
    {
        window = 0;
        if (!id.StartsWith(WINDOW_ID_PREFIX, StringComparison.Ordinal)
         || !long.TryParse(id.AsSpan(WINDOW_ID_PREFIX.Length), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out long value)) return false;

        window = (nint)value;
        return true;
    }

    /// <summary>
    /// Checks if the handle is the one of an existing top-level window (the only ones Windows.Graphics.Capture can capture).
    /// </summary>
    public static bool IsTopLevelWindow(nint window) => (window != 0) && IsWindow(window) && (GetAncestor(window, GA_ROOT) == window);

    /// <summary>
    /// Checks if the window is minimized.
    /// </summary>
    public static bool IsMinimized(nint window) => IsIconic(window);

    /// <summary>
    /// Gets the monitor (<c>HMONITOR</c>) the window is on (mostly), or the nearest one.
    /// </summary>
    public static nint GetMonitor(nint window) => MonitorFromWindow(window, MONITOR_DEFAULTTONEAREST);

    /// <summary>
    /// Creates the <see cref="Display"/> describing a window: its position on the desktop (the frame as it's shown, without the shadow), the given size (the one of its frames),
    /// its title as <see cref="Display.FriendlyName"/>, and the device name and graphics card of the display it's on.
    /// </summary>
    public static Display CreateDisplay(nint window, Display monitorDisplay, int width, int height)
    {
        (int x, int y) = GetPosition(window);
        return new Display(GetWindowId(window), -1, monitorDisplay.DeviceName, GetTitle(window), x, y, width, height, ImageRotation.None, monitorDisplay.GraphicsCard);
    }

    /// <summary>
    /// Creates the <see cref="Display"/> of a window capture before it knows the window (it isn't there anymore, or its display wasn't found): only the id is known.
    /// </summary>
    public static Display CreateUnknownDisplay(nint window)
        => new(GetWindowId(window), -1, string.Empty, string.Empty, 0, 0, 0, 0, ImageRotation.None, new GraphicsCard(string.Empty, -1, string.Empty, 0, 0));

    /// <summary>
    /// Gets the size of the window as it's shown (without the shadow), in pixels.
    /// </summary>
    public static (int Width, int Height) GetSize(nint window)
    {
        RECT bounds = GetBounds(window);
        return (bounds.Right - bounds.Left, bounds.Bottom - bounds.Top);
    }

    private static (int X, int Y) GetPosition(nint window)
    {
        RECT bounds = GetBounds(window);
        return (bounds.Left, bounds.Top);
    }

    // The extended frame bounds are in physical pixels (also in a process that isn't DPI aware) and leave out the invisible resize borders and the shadow.
    private static unsafe RECT GetBounds(nint window)
    {
        RECT bounds;
        if ((DwmGetWindowAttribute(window, DWMWA_EXTENDED_FRAME_BOUNDS, &bounds, sizeof(RECT)) < 0) && !GetWindowRect(window, out bounds))
            return default;

        return bounds;
    }

    private static unsafe string GetTitle(nint window)
    {
        int length = GetWindowTextLengthW(window);
        if (length <= 0) return string.Empty;

        char[] buffer = new char[length + 1];
        fixed (char* text = buffer)
        {
            int copied = GetWindowTextW(window, text, buffer.Length);
            return new string(text, 0, Math.Max(0, copied));
        }
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindow(nint window);

    [LibraryImport("user32.dll")]
    private static partial nint GetAncestor(nint window, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsIconic(nint window);

    [LibraryImport("user32.dll")]
    private static partial nint MonitorFromWindow(nint window, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowRect(nint window, out RECT rect);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowTextLengthW")]
    private static partial int GetWindowTextLengthW(nint window);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowTextW")]
    private static unsafe partial int GetWindowTextW(nint window, char* text, int maxCount);

    [LibraryImport("dwmapi.dll")]
    private static unsafe partial int DwmGetWindowAttribute(nint window, int attribute, void* value, int size);

    #endregion

    #region Types

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    #endregion
}
