using System.Runtime.InteropServices;

// Only Windows honors these flags; on Linux the system loader (dlopen) resolves libX11 as before.
[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]

namespace ScreenCapture.NET;

internal static partial class X11
{
    internal const nint DISPLAY_NAME = 0;

    // AllPlanes (~0UL)
    internal static readonly CULong ALL_PLANES = new(nuint.MaxValue);
    internal const int ZPIXMAP = 2;
    internal const int LSB_FIRST = 0;

    [LibraryImport("libX11.so.6")]
    internal static partial X11DisplayHandle XOpenDisplay(nint displayName);

    [LibraryImport("libX11.so.6")]
    internal static partial int XScreenCount(X11DisplayHandle display);

    [LibraryImport("libX11.so.6")]
    internal static partial nint XScreenOfDisplay(X11DisplayHandle display, int screenNumber);

    [LibraryImport("libX11.so.6")]
    internal static partial int XWidthOfScreen(nint screen);

    [LibraryImport("libX11.so.6")]
    internal static partial int XHeightOfScreen(nint screen);

    [LibraryImport("libX11.so.6")]
    internal static partial CULong XRootWindowOfScreen(nint screen);

    [LibraryImport("libX11.so.6")]
    internal static partial X11ImageHandle XGetImage(X11DisplayHandle display, CULong drawable, int x, int y, uint width, uint height, CULong planeMask, int format);

    [LibraryImport("libX11.so.6")]
    internal static partial nint XGetSubImage(X11DisplayHandle display, CULong drawable, int x, int y, uint width, uint height, CULong planeMask, int format, X11ImageHandle image, int destX, int destY);

    [LibraryImport("libX11.so.6")]
    internal static partial int XDestroyImage(nint image);

    [LibraryImport("libX11.so.6")]
    internal static partial nint XDisplayString(X11DisplayHandle display);

    /// <summary>
    /// Installs a process-wide error handler (<c>int (*)(Display*, XErrorEvent*)</c>) and returns the previous one.
    /// </summary>
    [LibraryImport("libX11.so.6")]
    internal static partial nint XSetErrorHandler(nint handler);

    [LibraryImport("libX11.so.6")]
    internal static partial int XCloseDisplay(nint display);
    
    [StructLayout(LayoutKind.Sequential)]
    internal struct XErrorEvent
    {
        // ReSharper disable MemberCanBePrivate.Global
        public int type;
        public nint display;
        public CULong resourceid;
        public CULong serial;
        public byte error_code;
        public byte request_code;
        public byte minor_code;
        // ReSharper restore MemberCanBePrivate.Global
    }

    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct XImage
    {
        // ReSharper disable MemberCanBePrivate.Global
        public int width;
        public int height;
        public int xoffset;
        public int format;
        public byte* data;
        public int byte_order;
        public int bitmap_unit;
        public int bitmap_bit_order;
        public int bitmap_pad;
        public int depth;
        public int bytes_per_line;
        public int bits_per_pixel;
        public CULong red_mask;
        public CULong green_mask;
        public CULong blue_mask;
        public nint obdata;
        // ReSharper restore MemberCanBePrivate.Global
    }
}