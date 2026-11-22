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
/// Xlib's default handlers terminate the process, e.g. if <c>XGetImage</c> fails or the X server stops.
/// These record the errors of the displays of this library and pass all others to the handlers installed before.
/// Installed with the first display and never removed. Handlers installed later that don't chain to these disable them.
/// <para>
/// A broken connection can only be survived with <c>XSetIOErrorExitHandler</c> (libX11 1.7). Older versions terminate the process anyway,
/// so the I/O error handler isn't installed there and Xlib's default one still prints why.
/// </para>
/// </remarks>
internal static unsafe class X11ErrorHandler
{
    #region Properties & Fields

    private static readonly Lock INSTALL_LOCK = new();
    private static bool _isInstalled;

    // Only used under INSTALL_LOCK. libX11 1.7+ has exit handlers.
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

    // On the thread of the failing call. Must not throw.
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int OnError(nint display, X11.XErrorEvent* error) => ROUTER.OnError(display, error);

    // On the thread that found the broken connection, without the display locked.
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int OnIOError(nint display) => ROUTER.OnIOError(display);

    // Only for the displays of this library, after the I/O error handler. Returning makes the failing call fail.
    // Xlib locked the display before; without unlocking it, every other thread using or closing it waits forever.
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnIOErrorExit(nint display, nint userData) => X11.XUnlockDisplay(display);

    #endregion
}

/// <summary>
/// Records the X errors and broken connections of the registered displays and passes all others to <see cref="PreviousHandler"/> and <see cref="PreviousIOErrorHandler"/>.
/// </summary>
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
    /// Only if it's still <paramref name="errors"/>: a new display can get the same address and register before this is called.
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
        /// Gets or sets a value indicating whether the connection broke. Not reset by <see cref="Clear"/>.
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