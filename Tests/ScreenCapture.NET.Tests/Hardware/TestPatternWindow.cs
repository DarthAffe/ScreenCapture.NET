using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using HPPH;

namespace ScreenCapture.NET.Tests.Hardware;

/// <summary>
/// Shows a topmost window with a fixed image, so a capture of that area can be compared with known pixels.
/// </summary>
/// <remarks>
/// The window runs on its own thread, which is per-monitor DPI aware: its position and size are in physical pixels on the desktop, like the captured frames,
/// and the image is drawn 1:1. It doesn't take the focus.
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed class TestPatternWindow : IDisposable
{
    #region Constants

    private const uint WS_POPUP = 0x80000000;
    private const uint WS_OVERLAPPEDWINDOW = 0x00CF0000;
    private const uint WS_EX_TOPMOST = 0x00000008;
    private const uint WS_EX_TOOLWINDOW = 0x00000080;
    private const uint WS_EX_NOACTIVATE = 0x08000000;
    private const int SW_SHOWNOACTIVATE = 4;
    private const int SW_SHOWMINNOACTIVE = 7;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint WM_PAINT = 0x000F;
    private const uint PM_REMOVE = 0x0001;
    private const uint DIB_RGB_COLORS = 0;
    private const nint DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = -4;

    #endregion

    #region Properties & Fields

    // Owned while the window is shown, see the constructor.
    private readonly Mutex _otherWindows;

    private readonly Thread _thread;
    private readonly ManualResetEventSlim _shown = new();
    private volatile bool _stop;
    private Exception? _error;

    // Kept alive while the window class exists: Windows calls it. Every window has its own class, so no class outlives its delegate.
    private readonly WndProc _wndProc;
    private readonly string _className = $"ScreenCapture.NET.Tests.TestPatternWindow.{Guid.NewGuid():N}";

    private readonly ColorBGRA[] _pixels;
    private readonly int _width;
    private readonly int _height;
    private readonly string _title;
    private readonly bool _framed;
    private readonly bool _capturable;

    /// <summary>
    /// Gets the handle (<c>HWND</c>) of the window.
    /// </summary>
    public nint Handle { get; private set; }

    #endregion

    #region Constructors

    /// <summary>
    /// Shows the image (row by row, alpha ignored) at the given position on the desktop (physical pixels).
    /// </summary>
    /// <param name="x">The x-position of the window on the desktop.</param>
    /// <param name="y">The y-position of the window on the desktop.</param>
    /// <param name="width">The width of the image (and of the window without a frame).</param>
    /// <param name="height">The height of the image (and of the window without a frame).</param>
    /// <param name="pixels">The image.</param>
    /// <param name="title">The title of the window.</param>
    /// <param name="framed"><c>true</c> for a window with a frame (title bar and borders, in the given size), the image in its client area; <c>false</c> for a window that's only the image.</param>
    /// <param name="capturable">
    /// <c>true</c> for a normal window (with a taskbar button) that Windows.Graphics.Capture can capture as a window;
    /// <c>false</c> for a tool window that can't be activated, which it refuses (it's still part of the captures of displays).
    /// </param>
    /// <remarks>
    /// Only one test pattern window is shown at a time, also across processes (the test processes of both TFMs run at the same time and show their windows at the same places):
    /// it waits until the window of another process is gone. Dispose it on the thread that created it.
    /// </remarks>
    public TestPatternWindow(int x, int y, int width, int height, ColorBGRA[] pixels, string title = "", bool framed = false, bool capturable = false)
    {
        if (pixels.Length != (width * height)) throw new ArgumentException("The image doesn't have the size of the window.", nameof(pixels));

        _pixels = pixels;
        _width = width;
        _height = height;
        _title = title;
        _framed = framed;
        _capturable = capturable;

        _otherWindows = new Mutex(false, "ScreenCapture.NET.Tests.TestPatternWindow");
        try
        {
            _otherWindows.WaitOne();
        }
        catch (AbandonedMutexException)
        {
            // A test process ended while it showed a window: the mutex is owned now anyway.
        }

        try
        {
            _wndProc = WindowProc;
            _thread = new Thread(() => Run(x, y)) { IsBackground = true, Name = nameof(TestPatternWindow) };
            _thread.Start();

            if (!_shown.Wait(TimeSpan.FromSeconds(5)) || (_error != null))
                throw new InvalidOperationException("The test pattern window couldn't be shown.", _error);
        }
        catch
        {
            _otherWindows.ReleaseMutex();
            _otherWindows.Dispose();
            throw;
        }
    }

    #endregion

    #region Methods

    private void Run(int x, int y)
    {
        nint window = 0;
        nint instance = GetModuleHandleW(null);
        try
        {
            SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);

            WNDCLASSEXW windowClass = new()
            {
                cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
                hInstance = instance,
                lpszClassName = _className
            };
            if (RegisterClassExW(ref windowClass) == 0) throw new InvalidOperationException($"RegisterClassEx failed ({Marshal.GetLastPInvokeError()}).");

            uint exStyle = _capturable ? WS_EX_TOPMOST : WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
            window = CreateWindowExW(exStyle, _className, _title, _framed ? WS_OVERLAPPEDWINDOW : WS_POPUP, x, y, _width, _height, 0, 0, instance, 0);
            if (window == 0) throw new InvalidOperationException($"CreateWindowEx failed ({Marshal.GetLastPInvokeError()}).");

            ShowWindow(window, SW_SHOWNOACTIVATE);
            Handle = window;
            _shown.Set();

            while (!_stop)
            {
                while (PeekMessageW(out MSG message, 0, 0, 0, PM_REMOVE))
                {
                    TranslateMessage(ref message);
                    DispatchMessageW(ref message);
                }

                Thread.Sleep(10);
            }
        }
        catch (Exception ex)
        {
            _error = ex;
            _shown.Set();
        }
        finally
        {
            if (window != 0)
                DestroyWindow(window);
            UnregisterClassW(_className, instance);
        }
    }

    private unsafe nint WindowProc(nint window, uint message, nint wParam, nint lParam)
    {
        if (message != WM_PAINT) return DefWindowProcW(window, message, wParam, lParam);

        nint dc = BeginPaint(window, out PAINTSTRUCT paint);

        // 32 bits per pixel, top-down (negative height): the BGRA pixels as they are.
        BITMAPINFOHEADER header = new()
        {
            biSize = (uint)sizeof(BITMAPINFOHEADER),
            biWidth = _width,
            biHeight = -_height,
            biPlanes = 1,
            biBitCount = 32
        };
        fixed (ColorBGRA* pixels = _pixels)
            SetDIBitsToDevice(dc, 0, 0, (uint)_width, (uint)_height, 0, 0, 0, (uint)_height, pixels, ref header, DIB_RGB_COLORS);

        EndPaint(window, ref paint);
        return 0;
    }

    /// <summary>
    /// Changes the size of the window (the image stays at the top left; the rest of a larger window isn't painted).
    /// </summary>
    public void Resize(int width, int height) => Check(SetWindowPos(Handle, 0, 0, 0, width, height, SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE), nameof(SetWindowPos));

    /// <summary>
    /// Moves the window to the given position on the desktop (physical pixels).
    /// </summary>
    public void Move(int x, int y) => Check(SetWindowPos(Handle, 0, x, y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE), nameof(SetWindowPos));

    /// <summary>
    /// Minimizes the window.
    /// </summary>
    public void Minimize() => ShowWindow(Handle, SW_SHOWMINNOACTIVE);

    /// <summary>
    /// Shows a minimized window again, as it was before.
    /// </summary>
    public void Restore() => ShowWindow(Handle, SW_SHOWNOACTIVATE);

    private static void Check(bool success, string function)
    {
        if (!success) throw new InvalidOperationException($"{function} failed ({Marshal.GetLastPInvokeError()}).");
    }

    public void Dispose()
    {
        _stop = true;
        _thread.Join();
        _shown.Dispose();

        _otherWindows.ReleaseMutex();
        _otherWindows.Dispose();
    }

    #endregion

    #region Native

    private delegate nint WndProc(nint window, uint message, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEXW
    {
        public uint cbSize;
        public uint style;
        public nint lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public nint hInstance;
        public nint hIcon;
        public nint hCursor;
        public nint hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public nint hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int left, top, right, bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PAINTSTRUCT
    {
        public nint hdc;
        public int fErase;
        public RECT rcPaint;
        public int fRestore;
        public int fIncUpdate;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
        public byte[] rgbReserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public nint hwnd;
        public uint message;
        public nint wParam;
        public nint lParam;
        public uint time;
        public int ptX, ptY;
        public uint lPrivate;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

#pragma warning disable SYSLIB1054 // Delegates and the by-value array of PAINTSTRUCT need the classic marshalling.
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandleW(string? moduleName);

    [DllImport("user32.dll")]
    private static extern nint SetThreadDpiAwarenessContext(nint context);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassExW(ref WNDCLASSEXW windowClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool UnregisterClassW(string className, nint instance);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowExW(uint exStyle, string className, string windowName, uint style, int x, int y, int width, int height,
                                               nint parent, nint menu, nint instance, nint param);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(nint window, int command);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(nint window);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(nint window, nint insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool PeekMessageW(out MSG message, nint window, uint filterMin, uint filterMax, uint remove);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG message);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint DispatchMessageW(ref MSG message);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint DefWindowProcW(nint window, uint message, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern nint BeginPaint(nint window, out PAINTSTRUCT paint);

    [DllImport("user32.dll")]
    private static extern bool EndPaint(nint window, ref PAINTSTRUCT paint);

    [DllImport("gdi32.dll")]
    private static extern unsafe int SetDIBitsToDevice(nint dc, int xDest, int yDest, uint width, uint height, int xSrc, int ySrc, uint startScan, uint lines,
                                                       void* bits, ref BITMAPINFOHEADER header, uint colorUse);
#pragma warning restore SYSLIB1054

    #endregion
}
