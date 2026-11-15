using Microsoft.Win32.SafeHandles;

namespace ScreenCapture.NET;

/// <summary>
/// Owns a connection to an X server (a <c>Display*</c>) opened with <see cref="X11.XOpenDisplay"/>.
/// </summary>
/// <remarks>
/// If <see cref="X11.XOpenDisplay"/> fails, the handle is invalid (<see cref="System.Runtime.InteropServices.SafeHandle.IsInvalid"/>) and must not be used.
/// </remarks>
internal sealed class X11DisplayHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="X11DisplayHandle"/> class. Used by the interop marshaller.
    /// </summary>
    public X11DisplayHandle()
        : base(true)
    { }

    #endregion

    #region Methods

    /// <inheritdoc />
    protected override bool ReleaseHandle()
    {
        X11.XCloseDisplay(handle);
        return true;
    }

    #endregion
}