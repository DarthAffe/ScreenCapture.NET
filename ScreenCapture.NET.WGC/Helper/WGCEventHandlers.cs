using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace ScreenCapture.NET;

/// <summary>
/// <c>TypedEventHandler&lt;Direct3D11CaptureFramePool, Object&gt;</c> (the id of this specialization from <c>winrt/windows.graphics.capture.h</c>).
/// </summary>
[GeneratedComInterface]
[Guid("51a947f7-79cf-5a3e-a3a5-1289cfa6dfe8")]
internal partial interface IFrameArrivedHandler
{
    [PreserveSig]
    int Invoke(nint sender, nint args);
}

/// <summary>
/// <c>TypedEventHandler&lt;GraphicsCaptureItem, Object&gt;</c> (the id of this specialization from <c>winrt/windows.graphics.capture.h</c>).
/// </summary>
[GeneratedComInterface]
[Guid("e9c610c0-a68c-5bd9-8021-8589346eeee2")]
internal partial interface IItemClosedHandler
{
    [PreserveSig]
    int Invoke(nint sender, nint args);
}

/// <summary>
/// <c>IAgileObject</c>: the handler can be called on any thread (WinRT doesn't marshal calls to it).
/// </summary>
[GeneratedComInterface]
[Guid("94ea2b94-e9cc-49e0-c0ff-ee64ca8f5b90")]
internal partial interface IAgileObject;

/// <summary>
/// Handles <c>FrameArrived</c> of a frame pool, on a thread of Windows. Exceptions don't leave it.
/// </summary>
/// <param name="onFrameArrived">Called with the frame pool (a pointer that's only valid during the call).</param>
[GeneratedComClass]
internal sealed partial class FrameArrivedHandler(Action<nint> onFrameArrived) : IFrameArrivedHandler, IAgileObject
{
    public int Invoke(nint sender, nint args)
    {
        try { onFrameArrived(sender); } catch { /**/ }
        return 0;
    }
}

/// <summary>
/// Handles <c>Closed</c> of a capture item, on a thread of Windows. Exceptions don't leave it.
/// </summary>
[GeneratedComClass]
internal sealed partial class ItemClosedHandler(Action onClosed) : IItemClosedHandler, IAgileObject
{
    public int Invoke(nint sender, nint args)
    {
        try { onClosed(); } catch { /**/ }
        return 0;
    }
}
