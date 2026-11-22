using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ScreenCapture.NET;

/// <summary>
/// Owns one reference to a native WinRT (or COM) object.
/// </summary>
internal sealed class WinRTHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    #region Properties & Fields

    // Closing (IClosable) frees the resources even if Windows still holds references.
    private readonly bool _close;

    /// <summary>
    /// Gets the pointer to the object (the interface it was created with). Only valid while the handle isn't disposed.
    /// </summary>
    public nint Pointer => handle;

    #endregion

    #region Constructors

    /// <summary>
    /// Takes over a reference to a native object.
    /// </summary>
    /// <param name="pointer">The pointer, with a reference owned by the handle from now on.</param>
    /// <param name="close">If the object is closed (<c>IClosable</c>) before the reference is released.</param>
    public WinRTHandle(nint pointer, bool close)
        : base(true)
    {
        SetHandle(pointer);
        this._close = close;
    }

    #endregion

    #region Methods

    /// <inheritdoc />
    protected override bool ReleaseHandle()
    {
        if (_close)
            WGCInterop.TryClose(handle);

        Marshal.Release(handle);
        return true;
    }

    #endregion
}
