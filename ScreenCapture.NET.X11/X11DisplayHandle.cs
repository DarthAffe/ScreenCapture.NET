using System;
using Microsoft.Win32.SafeHandles;

namespace ScreenCapture.NET;

/// <summary>
/// Owns a connection to an X server (a <c>Display*</c>) opened with <see cref="Open"/>.
/// </summary>
/// <remarks>
/// If the connection can't be opened, the handle is invalid (<see cref="System.Runtime.InteropServices.SafeHandle.IsInvalid"/>) and must not be used.
/// </remarks>
internal sealed class X11DisplayHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    #region Properties & Fields

    private X11ErrorHandler.DisplayErrors? _errors;

    /// <summary>
    /// Gets the last X error of a request on this connection since <see cref="ClearError"/>.
    /// </summary>
    public X11Error? LastError => _errors?.LastError;

    #endregion

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="X11DisplayHandle"/> class. Used by the interop marshaller.
    /// </summary>
    public X11DisplayHandle()
        : base(true)
    { }

    #endregion

    #region Methods

    /// <summary>
    /// Opens a connection to an X server. X errors on it are recorded (<see cref="LastError"/>) instead of terminating the process.
    /// </summary>
    /// <param name="displayName">The name of the X display (e.g. <c>:0</c>); <c>null</c> for the one named by $DISPLAY.</param>
    /// <returns>The connection; invalid if it couldn't be opened.</returns>
    public static X11DisplayHandle Open(string? displayName)
    {
        X11DisplayHandle display = X11.XOpenDisplay(displayName);
        if (display.IsInvalid) return display;

        try
        {
            display._errors = X11ErrorHandler.Register(display.handle);
            return display;
        }
        catch
        {
            display.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Creates the exception for a connection that couldn't be opened (see <see cref="Open"/>).
    /// </summary>
    /// <param name="displayName">The name of the X display that was opened; <c>null</c> for the one named by $DISPLAY.</param>
    public static InvalidOperationException CreateOpenFailedException(string? displayName)
    {
        if (displayName != null)
            return new InvalidOperationException($"Couldn't connect to the X server '{displayName}'. Check that it's running and accessible; on Wayland, X11 capturing needs XWayland.");

        displayName = Environment.GetEnvironmentVariable("DISPLAY");
        return new InvalidOperationException(string.IsNullOrEmpty(displayName)
                                                 ? "Couldn't connect to an X server: $DISPLAY isn't set. On Wayland, X11 capturing needs XWayland."
                                                 : $"Couldn't connect to the X server '{displayName}' ($DISPLAY). Check that it's running and accessible; on Wayland, X11 capturing needs XWayland.");
    }

    /// <summary>
    /// Clears <see cref="LastError"/>.
    /// </summary>
    public void ClearError() => _errors?.Clear();

    /// <inheritdoc />
    protected override bool ReleaseHandle()
    {
        _ = X11.XCloseDisplay(handle);

        // Only after closing: XCloseDisplay flushes pending requests, and their errors must not reach Xlib's default handler.
        if (_errors != null)
            X11ErrorHandler.Unregister(handle, _errors);

        return true;
    }

    #endregion
}