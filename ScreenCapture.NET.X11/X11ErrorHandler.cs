using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace ScreenCapture.NET;

/// <summary>
/// An X error reported for a request.
/// </summary>
/// <param name="ErrorCode">The error code, e.g. 8 (BadMatch).</param>
/// <param name="RequestCode">The major opcode of the failed request, e.g. 73 (X_GetImage).</param>
/// <param name="MinorCode">The minor opcode of the failed request.</param>
internal sealed record X11Error(byte ErrorCode, byte RequestCode, byte MinorCode)
{
    /// <inheritdoc />
    public override string ToString() => $"X error {ErrorCode} (request {RequestCode}, minor {MinorCode})";
}

/// <summary>
/// The process-wide Xlib error handlers: <c>XSetErrorHandler</c> for errors of requests and, with libX11 1.7 or newer, <c>XSetIOErrorHandler</c> for broken connections.
/// </summary>
/// <remarks>
/// Xlib's default handlers print the error and terminate the process, e.g. if <c>XGetImage</c> fails because the screen got smaller, or if the X server stops.
/// These handlers record errors for the displays opened by this library (<see cref="X11DisplayHandle.Open"/>) and pass errors for any other display
/// to the handlers that were installed before, so other X11 code in the process keeps its behavior.
/// They're installed once, when the first display is opened, and never removed.
/// Code that installs its own handlers afterwards and doesn't chain to the previous ones disables them.
/// <para>
/// A broken connection can only be survived with an exit handler for the display (<c>XSetIOErrorExitHandler</c>, libX11 1.7):
/// older versions terminate the process after any I/O error handler returned. There the I/O error handler isn't installed, so Xlib's default handler still prints why.
/// </para>
/// </remarks>
internal static unsafe class X11ErrorHandler
{
    #region Properties & Fields

    private static readonly Lock INSTALL_LOCK = new();
    private static bool _isInstalled;

    // Only used under INSTALL_LOCK. Whether libX11 has exit handlers (1.7+), so broken connections can be survived.
    private static bool _survivesBrokenConnections;

    private static readonly X11ErrorRouter ROUTER = new();

    #endregion

    #region Methods

    /// <summary>
    /// Installs the handlers (once) and records errors for the given display from now on.
    /// </summary>
    /// <param name="display">The <c>Display*</c>.</param>
    /// <returns>The errors of the display.</returns>
    internal static X11ErrorRouter.DisplayErrors Register(nint display)
    {
        lock (INSTALL_LOCK)
        {
            if (!_isInstalled)
            {
                // Return Xlib's default handlers if none were installed.
                ROUTER.PreviousHandler = X11.XSetErrorHandler((nint)(delegate* unmanaged[Cdecl]<nint, X11.XErrorEvent*, int>)&OnError);

                _survivesBrokenConnections = TrySetExitHandler(display);
                if (_survivesBrokenConnections)
                    ROUTER.PreviousIOErrorHandler = X11.XSetIOErrorHandler((nint)(delegate* unmanaged[Cdecl]<nint, int>)&OnIOError);

                _isInstalled = true;
            }
            else if (_survivesBrokenConnections)
                _ = TrySetExitHandler(display);
        }

        return ROUTER.Register(display);
    }

    /// <inheritdoc cref="X11ErrorRouter.Unregister"/>
    internal static void Unregister(nint display, X11ErrorRouter.DisplayErrors errors) => ROUTER.Unregister(display, errors);

    private static bool TrySetExitHandler(nint display)
    {
        try
        {
            X11.XSetIOErrorExitHandler(display, (nint)(delegate* unmanaged[Cdecl]<nint, nint, void>)&OnIOErrorExit, 0);
            return true;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
    }

    // Called by Xlib on the thread that made the failing call. Must not throw: an exception can't cross into native code.
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int OnError(nint display, X11.XErrorEvent* error) => ROUTER.OnError(display, error);

    // Called by Xlib on the thread that found the broken connection, without the display locked.
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int OnIOError(nint display) => ROUTER.OnIOError(display);

    // Only set for the displays of this library. Called after the I/O error handler returned, on the same thread.
    // Returning makes the failing call return a failure. Xlib locked the display for this thread before calling the handlers (to keep other threads out while the process exits):
    // without unlocking it, any other thread that uses or closes the display waits forever.
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnIOErrorExit(nint display, nint userData) => X11.XUnlockDisplay(display);

    #endregion
}

/// <summary>
/// Records the X errors and broken connections of the registered displays and passes those of any other display to <see cref="PreviousHandler"/> and <see cref="PreviousIOErrorHandler"/>.
/// </summary>
/// <remarks>
/// <see cref="X11ErrorHandler"/> routes the errors of the process through one instance.
/// </remarks>
internal sealed unsafe class X11ErrorRouter
{
    #region Properties & Fields

    private readonly ConcurrentDictionary<nint, DisplayErrors> _displays = new();

    private nint _previousHandler;
    private nint _previousIOErrorHandler;

    /// <summary>
    /// Gets or sets the handler errors for other displays are passed to (a <c>XErrorHandler</c>); <c>0</c> to drop them.
    /// </summary>
    public nint PreviousHandler
    {
        get => Volatile.Read(ref _previousHandler);
        set => Volatile.Write(ref _previousHandler, value);
    }

    /// <summary>
    /// Gets or sets the handler broken connections of other displays are passed to (a <c>XIOErrorHandler</c>); <c>0</c> to drop them.
    /// </summary>
    public nint PreviousIOErrorHandler
    {
        get => Volatile.Read(ref _previousIOErrorHandler);
        set => Volatile.Write(ref _previousIOErrorHandler, value);
    }

    #endregion

    #region Methods

    /// <summary>
    /// Records errors for the given display from now on.
    /// </summary>
    /// <param name="display">The <c>Display*</c>.</param>
    /// <returns>The errors of the display.</returns>
    public DisplayErrors Register(nint display)
    {
        DisplayErrors errors = new();
        _displays[display] = errors;
        return errors;
    }

    /// <summary>
    /// Stops recording errors for the given display.
    /// </summary>
    /// <remarks>
    /// Only removes the registration if it's still <paramref name="errors"/>:
    /// after the display is closed, a new display can get the same address and register it before this is called.
    /// </remarks>
    /// <param name="display">The <c>Display*</c>.</param>
    /// <param name="errors">The errors returned by <see cref="Register"/>.</param>
    public void Unregister(nint display, DisplayErrors errors)
        => _displays.TryRemove(new KeyValuePair<nint, DisplayErrors>(display, errors));

    /// <summary>
    /// Handles an error like an Xlib error handler. Doesn't throw.
    /// </summary>
    /// <param name="display">The <c>Display*</c> of the failed request.</param>
    /// <param name="error">The error.</param>
    /// <returns>The result of <see cref="PreviousHandler"/> for other displays; otherwise <c>0</c> (ignored by Xlib).</returns>
    public int OnError(nint display, X11.XErrorEvent* error)
    {
        if (_displays.TryGetValue(display, out DisplayErrors? errors))
        {
            errors.LastError = new X11Error(error->error_code, error->request_code, error->minor_code);
            return 0;
        }

        nint previousHandler = PreviousHandler;
        return previousHandler == 0 ? 0 : ((delegate* unmanaged[Cdecl]<nint, X11.XErrorEvent*, int>)previousHandler)(display, error);
    }

    /// <summary>
    /// Handles a broken connection like an Xlib I/O error handler. Doesn't throw.
    /// </summary>
    /// <param name="display">The <c>Display*</c> of the broken connection.</param>
    /// <returns>The result of <see cref="PreviousIOErrorHandler"/> for other displays; otherwise <c>0</c> (ignored by Xlib).</returns>
    public int OnIOError(nint display)
    {
        if (_displays.TryGetValue(display, out DisplayErrors? errors))
        {
            errors.IsConnectionLost = true;
            return 0;
        }

        nint previousHandler = PreviousIOErrorHandler;
        return previousHandler == 0 ? 0 : ((delegate* unmanaged[Cdecl]<nint, int>)previousHandler)(display);
    }

    #endregion

    /// <summary>
    /// The errors recorded for one display.
    /// </summary>
    internal sealed class DisplayErrors
    {
        private X11Error? _lastError;
        private bool _isConnectionLost;

        /// <summary>
        /// Gets or sets the last error since <see cref="Clear"/>.
        /// </summary>
        public X11Error? LastError
        {
            get => Volatile.Read(ref _lastError);
            set => Volatile.Write(ref _lastError, value);
        }

        /// <summary>
        /// Gets or sets a value indicating whether the connection broke. It stays broken: <see cref="Clear"/> doesn't reset it.
        /// </summary>
        public bool IsConnectionLost
        {
            get => Volatile.Read(ref _isConnectionLost);
            set => Volatile.Write(ref _isConnectionLost, value);
        }

        /// <summary>
        /// Clears <see cref="LastError"/>.
        /// </summary>
        public void Clear() => LastError = null;
    }
}