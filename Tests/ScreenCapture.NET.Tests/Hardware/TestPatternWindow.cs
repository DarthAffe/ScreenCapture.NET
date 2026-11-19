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
    private const uint WS_EX_TOPMOST = 0x00000008;
    private const uint WS_EX_TOOLWINDOW = 0x00000080;
    private const uint WS_EX_NOACTIVATE = 0x08000000;
    private const int SW_SHOWNOACTIVATE = 4;
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

    #endregion

    #region Constructors

    /// <summary>
    /// Shows the image (row by row, alpha ignored) at the given position on the desktop (physical pixels).
    /// </summary>
    /// <remarks>
    /// Only one test pattern window is shown at a time, also across processes (the test processes of both TFMs run at the same time and show their windows at the same places):
    /// it waits until the window of another process is gone. Dispose it on the thread that created it.
    /// </remarks>
    public TestPatternWindow(int x, int y, int width, int height, ColorBGRA[] pixels)
    {
        if (pixels.Length != (width * height)) throw new ArgumentException("The image doesn't have the size of the window.", nameof(pixels));

        _pixels = pixels;
        _width = width;
        _height = height;

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

            window = CreateWindowExW(WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE, _className, "", WS_POPUP, x, y, _width, _height, 0, 0, instance, 0);
            if (window == 0) throw new InvalidOperationException($"CreateWindowEx failed ({Marshal.GetLastPInvokeError()}).");

            ShowWindow(window, SW_SHOWNOACTIVATE);
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
