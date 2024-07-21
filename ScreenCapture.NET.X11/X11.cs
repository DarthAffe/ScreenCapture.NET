using System.Runtime.InteropServices;

namespace ScreenCapture.NET;

internal static partial class X11
{
    internal const nint DISPLAY_NAME = 0;

    internal const long ALL_PLANES = -1;
    internal const int ZPIXMAP = 2;

    [LibraryImport("libX11.so.6")]
    internal static partial nint XOpenDisplay(nint displayName);

    [LibraryImport("libX11.so.6")]
    internal static partial int XScreenCount(nint display);

    [LibraryImport("libX11.so.6")]
    internal static partial nint XScreenOfDisplay(nint display, int screeenNumber);

    [LibraryImport("libX11.so.6")]
    internal static partial int XWidthOfScreen(nint screen);

    [LibraryImport("libX11.so.6")]
    internal static partial int XHeightOfScreen(nint screen);

    [LibraryImport("libX11.so.6")]
    internal static partial nint XRootWindowOfScreen(nint screen);

    [LibraryImport("libX11.so.6")]
    internal static partial nint XGetImage(nint display, nint drawable, int x, int y, uint width, uint height, long planeMask, int format);

    [LibraryImport("libX11.so.6")]
    internal static partial nint XGetSubImage(nint display, nint drawable, int x, int y, uint width, uint height, long planeMask, int format, nint image, int destX, int dextY);

    [LibraryImport("libX11.so.6")]
    internal static partial void XDestroyImage(nint image);

    [LibraryImport("libX11.so.6")]
    internal static partial nint XDisplayString(nint display);

    [LibraryImport("libX11.so.6")]
    internal static partial void XCloseDisplay(nint display);

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
        public uint red_mask;
        public uint green_mask;
        public uint blue_mask;
        public nint obdata;
        // ReSharper restore MemberCanBePrivate.Global
    }
}