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
/// The process-wide Xlib error handler (<c>XSetErrorHandler</c>).
/// </summary>
/// <remarks>
/// Xlib's default handler prints the error and terminates the process, e.g. if <c>XGetImage</c> fails because the screen got smaller.
/// This handler records errors for the displays opened by this library (<see cref="X11DisplayHandle.Open"/>) and passes errors for any other display
/// to the handler that was installed before, so other X11 code in the process keeps its behavior.
/// It's installed once, when the first display is opened, and never removed.
/// Code that installs its own handler afterwards and doesn't chain to the previous one disables it.
/// </remarks>
internal static unsafe class X11ErrorHandler
{
    #region Properties & Fields

    private static readonly Lock INSTALL_LOCK = new();
    private static bool _isInstalled;

    private static readonly ConcurrentDictionary<nint, DisplayErrors> DISPLAYS = new();

    private static nint _previousHandler;

    /// <summary>
    /// Gets or sets the handler errors for unknown displays are passed to. Set by the installation; tests can replace it.
    /// </summary>
    internal static nint PreviousHandler
    {
        get => Volatile.Read(ref _previousHandler);
        set => Volatile.Write(ref _previousHandler, value);
    }

    /// <summary>
    /// Gets the handler function.
    /// </summary>
    internal static delegate* unmanaged[Cdecl]<nint, X11.XErrorEvent*, int> Handler => &OnError;

    #endregion

    #region Methods

    /// <summary>
    /// Installs the handler (once) and records errors for the given display from now on.
    /// </summary>
    /// <param name="display">The <c>Display*</c>.</param>
    /// <returns>The errors of the display.</returns>
    internal static DisplayErrors Register(nint display)
    {
        EnsureInstalled();
        return Track(display);
    }

    /// <summary>
    /// Records errors for the given display without installing the handler.
    /// </summary>
    /// <param name="display">The <c>Display*</c>.</param>
    /// <returns>The errors of the display.</returns>
    internal static DisplayErrors Track(nint display)
    {
        DisplayErrors errors = new();
        DISPLAYS[display] = errors;
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
    internal static void Unregister(nint display, DisplayErrors errors)
        => DISPLAYS.TryRemove(new KeyValuePair<nint, DisplayErrors>(display, errors));

    private static void EnsureInstalled()
    {
        lock (INSTALL_LOCK)
        {
            if (_isInstalled) return;

            // Returns Xlib's default handler if none was installed.
            PreviousHandler = X11.XSetErrorHandler((nint)Handler);
            _isInstalled = true;
        }
    }

    // Called by Xlib on the thread that made the failing call. Must not throw: an exception can't cross into native code.
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int OnError(nint display, X11.XErrorEvent* error)
    {
        if (DISPLAYS.TryGetValue(display, out DisplayErrors? errors))
        {
            errors.LastError = new X11Error(error->error_code, error->request_code, error->minor_code);
            return 0;
        }

        nint previousHandler = PreviousHandler;
        return previousHandler == 0 ? 0 : ((delegate* unmanaged[Cdecl]<nint, X11.XErrorEvent*, int>)previousHandler)(display, error);
    }

    #endregion

    /// <summary>
    /// The errors recorded for one display.
    /// </summary>
    internal sealed class DisplayErrors
    {
        private X11Error? _lastError;

        /// <summary>
        /// Gets or sets the last error since <see cref="Clear"/>.
        /// </summary>
        public X11Error? LastError
        {
            get => Volatile.Read(ref _lastError);
            set => Volatile.Write(ref _lastError, value);
        }

        /// <summary>
        /// Clears <see cref="LastError"/>.
        /// </summary>
        public void Clear() => LastError = null;
    }
}