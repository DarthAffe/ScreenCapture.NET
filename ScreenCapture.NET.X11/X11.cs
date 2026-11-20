using System.Runtime.InteropServices;

// Only Windows honors these flags; on Linux the system loader (dlopen) resolves libX11 as before.
[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]

namespace ScreenCapture.NET;

internal static partial class X11
{
    // AllPlanes (~0UL)
    internal static readonly CULong ALL_PLANES = new(nuint.MaxValue);
    internal const int ZPIXMAP = 2;
    internal const int LSB_FIRST = 0;

    // null opens the display named by $DISPLAY.
    [LibraryImport("libX11.so.6", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial X11DisplayHandle XOpenDisplay(string? displayName);

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
    internal static partial nint XDefaultVisualOfScreen(nint screen);

    [LibraryImport("libX11.so.6")]
    internal static partial int XDefaultDepthOfScreen(nint screen);

    [LibraryImport("libX11.so.6")]
    internal static partial int XSync(X11DisplayHandle display, [MarshalAs(UnmanagedType.Bool)] bool discard);

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

    #region MIT-SHM (libXext)

    [LibraryImport("libXext.so.6")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool XShmQueryExtension(X11DisplayHandle display);

    /// <summary>
    /// Creates an image without pixel memory; the image keeps the pointer to <paramref name="segment"/> (<see cref="XImage.obdata"/>).
    /// </summary>
    [LibraryImport("libXext.so.6")]
    internal static unsafe partial X11ImageHandle XShmCreateImage(X11DisplayHandle display, nint visual, uint depth, int format, nint data, XShmSegmentInfo* segment, uint width, uint height);

    /// <summary>
    /// Asks the server to attach the segment. Only returns <c>false</c> if the request couldn't be sent: a server that can't attach it reports BadAccess asynchronously.
    /// </summary>
    [LibraryImport("libXext.so.6")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static unsafe partial bool XShmAttach(X11DisplayHandle display, XShmSegmentInfo* segment);

    // Takes the Display* itself: called while the image handle is released (see X11ImageHandle).
    [LibraryImport("libXext.so.6")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static unsafe partial bool XShmDetach(nint display, XShmSegmentInfo* segment);

    [LibraryImport("libXext.so.6")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool XShmGetImage(X11DisplayHandle display, CULong drawable, X11ImageHandle image, int x, int y, CULong planeMask);

    #endregion

    #region System V shared memory (libc)

    internal const int IPC_PRIVATE = 0;
    internal const int IPC_CREAT = 0x200; // 01000
    internal const int IPC_RMID = 0;
    internal const int SHM_MODE_OWNER_READ_WRITE = 0x180; // 0600

    [LibraryImport("libc")]
    internal static partial int shmget(int key, nuint size, int flags);

    [LibraryImport("libc")]
    internal static partial nint shmat(int id, nint address, int flags);

    [LibraryImport("libc")]
    internal static partial int shmdt(nint address);

    [LibraryImport("libc")]
    internal static partial int shmctl(int id, int command, nint buffer);

    #endregion

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

    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct XShmSegmentInfo
    {
        // ReSharper disable MemberCanBePrivate.Global
        public CULong shmseg;
        public int shmid;
        public byte* shmaddr;
        public int readOnly;
        // ReSharper restore MemberCanBePrivate.Global
    }
}