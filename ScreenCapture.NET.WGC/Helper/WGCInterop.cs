using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Threading;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace ScreenCapture.NET;

/// <summary>
/// The calls into Windows.Graphics.Capture (WinRT) and its Direct3D 11 interop, as plain COM calls through the vtables of the interfaces.
/// </summary>
/// <remarks>
/// The interface ids and the order of their methods are the ones of the Windows SDK headers (<c>winrt/windows.graphics.capture.h</c>, <c>winrt/windows.foundation.h</c>,
/// <c>winrt/windows.graphics.directx.direct3d11.h</c>, <c>um/windows.graphics.capture.interop.h</c>, <c>um/windows.graphics.directx.direct3d11.interop.h</c>).
/// WinRT interfaces derive from <c>IInspectable</c>: their own methods start after its 6 (<c>IUnknown</c>'s 3 and <c>GetIids</c>, <c>GetRuntimeClassName</c>, <c>GetTrustLevel</c>).
/// </remarks>
// ReSharper disable InconsistentNaming
internal static unsafe partial class WGCInterop
{
    #region Constants

    private const string GRAPHICS_CAPTURE_SESSION = "Windows.Graphics.Capture.GraphicsCaptureSession";
    private const string GRAPHICS_CAPTURE_ITEM = "Windows.Graphics.Capture.GraphicsCaptureItem";
    private const string DIRECT3D11_CAPTURE_FRAME_POOL = "Windows.Graphics.Capture.Direct3D11CaptureFramePool";

    private static readonly Guid IID_IGraphicsCaptureSessionStatics = new("2224a540-5974-49aa-b232-0882536f4cb5");
    private static readonly Guid IID_IGraphicsCaptureSession2 = new("2c39ae40-7d2e-5044-804e-8b6799d4cf9e");
    private static readonly Guid IID_IGraphicsCaptureSession3 = new("f2cdd966-22ae-5ea1-9596-3a289344c3be");
    private static readonly Guid IID_IGraphicsCaptureSession5 = new("67c0ea62-1f85-5061-925a-239be0ac09cb");
    private static readonly Guid IID_IGraphicsCaptureItem = new("79c3f95b-31f7-4ec2-a464-632ef5d30760");
    private static readonly Guid IID_IGraphicsCaptureItemInterop = new("3628e81b-3cac-4c60-b7f4-23ce0e0c3356");
    private static readonly Guid IID_IDirect3D11CaptureFramePoolStatics2 = new("589b103f-6bbc-5df5-a991-02e28b3b66d5");
    private static readonly Guid IID_IDirect3DDevice = new("a37624ab-8d5f-4650-9d3e-9eae3d9bc670");
    private static readonly Guid IID_IDirect3DDxgiInterfaceAccess = new("a9b3d012-3df2-4ee3-b8d1-8695f457d3c1");
    private static readonly Guid IID_IClosable = new("30d5a829-7fa4-4026-83bb-d75bae4ea99e");

    // The first method of a WinRT interface (after IUnknown and IInspectable) and of a COM interface (after IUnknown).
    private const int WINRT = 6;
    private const int COM = 3;

    /// <summary>
    /// <c>DirectXPixelFormat.B8G8R8A8UIntNormalized</c> (the value of <c>DXGI_FORMAT_B8G8R8A8_UNORM</c>).
    /// </summary>
    public const int PIXEL_FORMAT_BGRA8 = 87;

    /// <summary>
    /// <c>DirectXPixelFormat.R16G16B16A16Float</c> (the value of <c>DXGI_FORMAT_R16G16B16A16_FLOAT</c>): scRGB, used for HDR.
    /// </summary>
    public const int PIXEL_FORMAT_RGBA16F = 10;

    #endregion

    #region Properties & Fields

    private static readonly Lock MTA_LOCK = new();
    private static bool _mtaUsed;

    #endregion

    #region Methods

    /// <summary>
    /// Checks if Windows.Graphics.Capture is supported (<c>GraphicsCaptureSession.IsSupported</c>); <c>false</c> if it doesn't exist on this Windows.
    /// </summary>
    public static bool IsSupported()
    {
        nint statics;
        try
        {
            statics = GetActivationFactory(GRAPHICS_CAPTURE_SESSION, IID_IGraphicsCaptureSessionStatics);
        }
        catch (Exception)
        {
            return false;
        }

        try
        {
            byte supported;
            Check(((delegate* unmanaged[Stdcall]<nint, byte*, int>)VTable(statics)[WINRT])(statics, &supported));
            return supported != 0;
        }
        finally
        {
            Marshal.Release(statics);
        }
    }

    /// <summary>
    /// Creates the capture item (<c>IGraphicsCaptureItem</c>) of a monitor.
    /// </summary>
    /// <param name="monitor">The <c>HMONITOR</c> of the monitor (e.g. <see cref="OutputDescription.Monitor"/>).</param>
    public static WinRTHandle CreateItemForMonitor(nint monitor)
    {
        nint interop = GetActivationFactory(GRAPHICS_CAPTURE_ITEM, IID_IGraphicsCaptureItemInterop);
        try
        {
            nint item;
            Guid iid = IID_IGraphicsCaptureItem;
            // IGraphicsCaptureItemInterop: CreateForWindow, CreateForMonitor.
            Check(((delegate* unmanaged[Stdcall]<nint, nint, Guid*, nint*, int>)VTable(interop)[COM + 1])(interop, monitor, &iid, &item));
            return new WinRTHandle(item, close: false);
        }
        finally
        {
            Marshal.Release(interop);
        }
    }

    /// <summary>
    /// Creates the capture item (<c>IGraphicsCaptureItem</c>) of a top-level window.
    /// </summary>
    /// <param name="window">The <c>HWND</c> of the window.</param>
    public static WinRTHandle CreateItemForWindow(nint window)
    {
        nint interop = GetActivationFactory(GRAPHICS_CAPTURE_ITEM, IID_IGraphicsCaptureItemInterop);
        try
        {
            nint item;
            Guid iid = IID_IGraphicsCaptureItem;
            // IGraphicsCaptureItemInterop: CreateForWindow, CreateForMonitor.
            Check(((delegate* unmanaged[Stdcall]<nint, nint, Guid*, nint*, int>)VTable(interop)[COM])(interop, window, &iid, &item));
            return new WinRTHandle(item, close: false);
        }
        finally
        {
            Marshal.Release(interop);
        }
    }

    /// <summary>
    /// Gets the size of a capture item (<c>IGraphicsCaptureItem.Size</c>).
    /// </summary>
    public static SizeInt32 GetSize(WinRTHandle item)
    {
        SizeInt32 size;
        Check(((delegate* unmanaged[Stdcall]<nint, SizeInt32*, int>)VTable(item.Pointer)[WINRT + 1])(item.Pointer, &size));
        return size;
    }

    /// <summary>
    /// Adds a handler to the <c>Closed</c> event of a capture item.
    /// </summary>
    /// <returns>The token to remove it again.</returns>
    public static long AddClosed(WinRTHandle item, ItemClosedHandler handler)
    {
        nint handlerPointer = (nint)ComInterfaceMarshaller<IItemClosedHandler>.ConvertToUnmanaged(handler);
        try
        {
            long token;
            Check(((delegate* unmanaged[Stdcall]<nint, nint, long*, int>)VTable(item.Pointer)[WINRT + 2])(item.Pointer, handlerPointer, &token));
            return token;
        }
        finally
        {
            // The item holds its own reference.
            Marshal.Release(handlerPointer);
        }
    }

    public static void RemoveClosed(WinRTHandle item, long token)
        => Check(((delegate* unmanaged[Stdcall]<nint, long, int>)VTable(item.Pointer)[WINRT + 3])(item.Pointer, token));

    /// <summary>
    /// Creates the WinRT device (<c>IDirect3DDevice</c>, used by the frame pool) of a Direct3D 11 device.
    /// </summary>
    public static WinRTHandle CreateDirect3DDevice(ID3D11Device device)
    {
        using IDXGIDevice dxgiDevice = device.QueryInterface<IDXGIDevice>();
        Check(CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice.NativePointer, out nint inspectable));
        try
        {
            return new WinRTHandle(QueryInterface(inspectable, IID_IDirect3DDevice), close: true);
        }
        finally
        {
            Marshal.Release(inspectable);
        }
    }

    /// <summary>
    /// Creates a free-threaded frame pool (<c>Direct3D11CaptureFramePool.CreateFreeThreaded</c>): its events are raised on threads of Windows.
    /// </summary>
    public static WinRTHandle CreateFreeThreadedFramePool(WinRTHandle device, int pixelFormat, int numberOfBuffers, SizeInt32 size)
    {
        nint statics = GetActivationFactory(DIRECT3D11_CAPTURE_FRAME_POOL, IID_IDirect3D11CaptureFramePoolStatics2);
        try
        {
            nint framePool;
            Check(((delegate* unmanaged[Stdcall]<nint, nint, int, int, SizeInt32, nint*, int>)VTable(statics)[WINRT])(statics, device.Pointer, pixelFormat, numberOfBuffers, size, &framePool));
            return new WinRTHandle(framePool, close: true);
        }
        finally
        {
            Marshal.Release(statics);
        }
    }

    /// <summary>
    /// Gives a frame pool new buffers (<c>IDirect3D11CaptureFramePool.Recreate</c>), e.g. of another size. Its session and event handlers stay.
    /// </summary>
    public static void RecreateFramePool(WinRTHandle framePool, WinRTHandle device, int pixelFormat, int numberOfBuffers, SizeInt32 size)
        => Check(((delegate* unmanaged[Stdcall]<nint, nint, int, int, SizeInt32, int>)VTable(framePool.Pointer)[WINRT])(framePool.Pointer, device.Pointer, pixelFormat, numberOfBuffers, size));

    /// <summary>
    /// Takes the next frame (<c>IDirect3D11CaptureFrame</c>) out of a frame pool (<c>IDirect3D11CaptureFramePool.TryGetNextFrame</c>).
    /// </summary>
    /// <returns>The frame (give it back with <see cref="ReleaseFrame"/>), or 0 if there's none.</returns>
    public static nint TryGetNextFrame(nint framePool)
    {
        nint frame;
        Check(((delegate* unmanaged[Stdcall]<nint, nint*, int>)VTable(framePool)[WINRT + 1])(framePool, &frame));
        return frame;
    }

    /// <summary>
    /// Adds a handler to the <c>FrameArrived</c> event of a frame pool.
    /// </summary>
    /// <returns>The token to remove it again.</returns>
    public static long AddFrameArrived(WinRTHandle framePool, FrameArrivedHandler handler)
    {
        nint handlerPointer = (nint)ComInterfaceMarshaller<IFrameArrivedHandler>.ConvertToUnmanaged(handler);
        try
        {
            long token;
            Check(((delegate* unmanaged[Stdcall]<nint, nint, long*, int>)VTable(framePool.Pointer)[WINRT + 2])(framePool.Pointer, handlerPointer, &token));
            return token;
        }
        finally
        {
            // The frame pool holds its own reference.
            Marshal.Release(handlerPointer);
        }
    }

    public static void RemoveFrameArrived(WinRTHandle framePool, long token)
        => Check(((delegate* unmanaged[Stdcall]<nint, long, int>)VTable(framePool.Pointer)[WINRT + 3])(framePool.Pointer, token));

    /// <summary>
    /// Creates the capture session (<c>IGraphicsCaptureSession</c>) of a frame pool for an item.
    /// </summary>
    public static WinRTHandle CreateCaptureSession(WinRTHandle framePool, WinRTHandle item)
    {
        nint session;
        Check(((delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)VTable(framePool.Pointer)[WINRT + 4])(framePool.Pointer, item.Pointer, &session));
        return new WinRTHandle(session, close: true);
    }

    public static void StartCapture(WinRTHandle session)
        => Check(((delegate* unmanaged[Stdcall]<nint, int>)VTable(session.Pointer)[WINRT])(session.Pointer));

    /// <summary>
    /// Sets if the mouse pointer is drawn into the frames (<c>IGraphicsCaptureSession2.IsCursorCaptureEnabled</c>).
    /// </summary>
    /// <returns><c>false</c> if this Windows doesn't support it (before Windows 10 2004).</returns>
    public static bool TrySetCursorCaptureEnabled(WinRTHandle session, bool enabled)
    {
        if (Marshal.QueryInterface(session.Pointer, IID_IGraphicsCaptureSession2, out nint session2) < 0) return false;
        try
        {
            Check(((delegate* unmanaged[Stdcall]<nint, byte, int>)VTable(session2)[WINRT + 1])(session2, enabled ? (byte)1 : (byte)0));
            return true;
        }
        finally
        {
            Marshal.Release(session2);
        }
    }

    /// <summary>
    /// Sets if Windows draws a border around the captured display (<c>IGraphicsCaptureSession3.IsBorderRequired</c>).
    /// </summary>
    /// <returns><c>false</c> if this Windows doesn't support it (Windows 10, where the border is always drawn).</returns>
    public static bool TrySetBorderRequired(WinRTHandle session, bool required)
    {
        if (Marshal.QueryInterface(session.Pointer, IID_IGraphicsCaptureSession3, out nint session3) < 0) return false;
        try
        {
            Check(((delegate* unmanaged[Stdcall]<nint, byte, int>)VTable(session3)[WINRT + 1])(session3, required ? (byte)1 : (byte)0));
            return true;
        }
        finally
        {
            Marshal.Release(session3);
        }
    }

    /// <summary>
    /// Sets the shortest time between two frames of the session (<c>IGraphicsCaptureSession5.MinUpdateInterval</c>).
    /// </summary>
    /// <returns><c>false</c> if this Windows doesn't support it (before Windows 11 24H2).</returns>
    public static bool TrySetMinUpdateInterval(WinRTHandle session, TimeSpan interval)
    {
        if (Marshal.QueryInterface(session.Pointer, IID_IGraphicsCaptureSession5, out nint session5) < 0) return false;
        try
        {
            // Windows.Foundation.TimeSpan is a struct with one Int64 (100 ns units), passed like an Int64.
            Check(((delegate* unmanaged[Stdcall]<nint, long, int>)VTable(session5)[WINRT + 1])(session5, interval.Ticks));
            return true;
        }
        finally
        {
            Marshal.Release(session5);
        }
    }

    /// <summary>
    /// Gets the surface (<c>IDirect3DSurface</c>) of a frame. The caller releases it.
    /// </summary>
    public static nint GetSurface(nint frame)
    {
        nint surface;
        Check(((delegate* unmanaged[Stdcall]<nint, nint*, int>)VTable(frame)[WINRT])(frame, &surface));
        return surface;
    }

    /// <summary>
    /// Gets the time of a frame (<c>SystemRelativeTime</c>, a QueryPerformanceCounter time in 100 ns units).
    /// </summary>
    public static long GetSystemRelativeTime(nint frame)
    {
        long time;
        Check(((delegate* unmanaged[Stdcall]<nint, long*, int>)VTable(frame)[WINRT + 1])(frame, &time));
        return time;
    }

    public static SizeInt32 GetContentSize(nint frame)
    {
        SizeInt32 size;
        Check(((delegate* unmanaged[Stdcall]<nint, SizeInt32*, int>)VTable(frame)[WINRT + 2])(frame, &size));
        return size;
    }

    /// <summary>
    /// Gets the Direct3D 11 texture of a frame. The caller disposes it.
    /// </summary>
    public static ID3D11Texture2D GetTexture(nint frame)
    {
        nint surface = GetSurface(frame);
        try
        {
            nint access = QueryInterface(surface, IID_IDirect3DDxgiInterfaceAccess);
            try
            {
                nint texture;
                Guid iid = typeof(ID3D11Texture2D).GUID;
                Check(((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)VTable(access)[COM])(access, &iid, &texture));

                // Owns the reference returned by GetInterface.
                return new ID3D11Texture2D(texture);
            }
            finally
            {
                Marshal.Release(access);
            }
        }
        finally
        {
            Marshal.Release(surface);
        }
    }

    /// <summary>
    /// Gives a frame back to its frame pool (closes it) and releases it.
    /// </summary>
    public static void ReleaseFrame(nint frame)
    {
        TryClose(frame);
        Marshal.Release(frame);
    }

    /// <summary>
    /// Closes an object (<c>IClosable.Close</c>), ignoring errors (e.g. an object that's closed already).
    /// </summary>
    public static void TryClose(nint inspectable)
    {
        if (Marshal.QueryInterface(inspectable, IID_IClosable, out nint closable) < 0) return;

        _ = ((delegate* unmanaged[Stdcall]<nint, int>)VTable(closable)[WINRT])(closable);
        Marshal.Release(closable);
    }

    private static nint GetActivationFactory(string className, Guid iid)
    {
        EnsureMTA();

        HStringHeader header;
        nint hstring;
        nint factory;
        fixed (char* name = className)
        {
            // A reference to the (null-terminated) string: nothing to free, but it's only valid while the string is pinned.
            Check(WindowsCreateStringReference(name, (uint)className.Length, &header, &hstring));
            Check(RoGetActivationFactory(hstring, &iid, &factory));
        }

        return factory;
    }

    // WinRT objects can only be used on threads that joined an apartment. With the multithreaded apartment kept alive for the process,
    // threads that never initialized COM (e.g. the main thread of a console application) belong to it implicitly.
    private static void EnsureMTA()
    {
        lock (MTA_LOCK)
        {
            if (_mtaUsed) return;

            // Never decremented: the apartment stays for the lifetime of the process.
            Check(CoIncrementMTAUsage(out _));
            _mtaUsed = true;
        }
    }

    private static nint QueryInterface(nint pointer, Guid iid)
    {
        Check(Marshal.QueryInterface(pointer, iid, out nint result));
        return result;
    }

    private static void** VTable(nint pointer) => *(void***)pointer;

    private static void Check(int result) => Marshal.ThrowExceptionForHR(result);

    [LibraryImport("combase.dll")]
    private static partial int WindowsCreateStringReference(char* sourceString, uint length, HStringHeader* header, nint* hstring);

    [LibraryImport("combase.dll")]
    private static partial int RoGetActivationFactory(nint activatableClassId, Guid* iid, nint* factory);

    [LibraryImport("combase.dll")]
    private static partial int CoIncrementMTAUsage(out nint cookie);

    [LibraryImport("d3d11.dll")]
    private static partial int CreateDirect3D11DeviceFromDXGIDevice(nint dxgiDevice, out nint graphicsDevice);

    #endregion

    #region Types

    /// <summary>
    /// <c>Windows.Graphics.SizeInt32</c>.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public readonly record struct SizeInt32(int Width, int Height);

    // HSTRING_HEADER: room for the string reference (24 bytes on 64-bit, 20 on 32-bit), only used by Windows.
    [StructLayout(LayoutKind.Sequential, Size = 24)]
    private struct HStringHeader;

    #endregion
}
