using System;
using System.Runtime.InteropServices;
using System.Threading;
using HPPH;

namespace ScreenCapture.NET;

/// <summary>
/// Represents a ScreenCapture using libX11, through shared memory (MIT-SHM) where the X server supports it (see <see cref="X11CaptureOptions.UseSharedMemory"/>).
/// https://x.org/releases/current/doc/libX11/libX11/libX11.html#XGetImage
/// </summary>
/// <remarks>
/// The first connection to the X server installs process-wide Xlib error handlers that stay installed: one for errors of requests (<c>XSetErrorHandler</c>)
/// and, with libX11 1.7 or newer, one for broken connections (<c>XSetIOErrorHandler</c>).
/// They handle the errors of this library's connections instead of Xlib terminating the process, and pass the errors of all other connections to the handlers that were installed before.
/// If other code installs its own handlers afterwards without passing errors on, the errors of the capture are handled by that code.
/// <para>
/// A failed request or a broken connection makes the capture restart. While the X server can't be reached (e.g. it stopped, or the network to a remote server failed),
/// the capture is <see cref="CaptureResult.Unavailable"/>; it captures again once the server is back.
/// With a libX11 older than 1.7, Xlib still terminates the process when a connection breaks.
/// A connection that stops responding without breaking (e.g. on a network that drops all packets) blocks the capture until the system gives up on it, which can take minutes.
/// </para>
/// <para>
/// A display is a RandR monitor, a rectangle of an X screen (see <see cref="X11ScreenCaptureService.GetDisplays"/>), and only that rectangle is read.
/// Every capture checks where the monitor is (one more round trip to the server): a monitor that was moved or resized makes the capture restart with it
/// (see <see cref="IScreenCapture.DisplayChanged"/>); one that was removed or turned off makes it unavailable (<see cref="CaptureResult.Unavailable"/>) until it's back.
/// </para>
/// <para>
/// A display that is a whole X screen (a server without RandR monitors) is read again by a restart: a screen that got smaller makes the capture fail and restart on its own;
/// one that got larger is captured with its previous size until <see cref="IScreenCapture.Restart"/> is called.
/// </para>
/// <para>
/// The pixels of the zones are opaque: their alpha is always 255, whatever the X server stores in the padding byte of a depth-24 visual.
/// </para>
/// </remarks>
// ReSharper disable once InconsistentNaming
public sealed class X11ScreenCapture : AbstractScreenCapture<ColorBGRA>
{
    #region Properties & Fields

    // Guards the handles: held by captures, zone updates and restarts.
    private readonly Lock _captureLock = new();

    // Only used under _captureLock.
    private X11DisplayHandle? _display;
    private CULong _drawable;

    // Only used under _captureLock. Holds the frame of the last capture.
    private X11ImageHandle? _image;

    // Only used under _captureLock. Where the display is on its X screen, and its RandR monitor (0 for a whole screen).
    private int _x;
    private int _y;
    private CULong _monitor;

    // The X display (server) to connect to; null for the one named by $DISPLAY.
    private readonly string? _displayName;

    /// <summary>
    /// Gets the options this capture was created with.
    /// </summary>
    public X11CaptureOptions Options { get; }

    #endregion

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="X11ScreenCapture"/> class.
    /// </summary>
    /// <param name="display">The <see cref="Display"/> to duplicate.</param>
    /// <param name="options">The options of the capture.</param>
    /// <param name="displayName">The name of the X display (e.g. <c>:0</c>); <c>null</c> for the one named by $DISPLAY.</param>
    internal X11ScreenCapture(Display display, X11CaptureOptions options, string? displayName)
        : base(display)
    {
        this.Options = options;
        this._displayName = displayName;

        Restart();
    }

    #endregion

    #region Methods

    /// <inheritdoc />
    protected override CaptureResult PerformScreenCapture(out long timestamp)
    {
        // X11 can't tell when the frame was presented: the base uses the time the capture returned.
        timestamp = 0;

        lock (_captureLock)
        {
            if ((_display == null) || (_image == null))
                return RestartCapture();

            // A monitor is only a rectangle of the screen: reading it after it was moved, resized or removed would succeed with the wrong pixels.
            if ((_monitor.Value != 0) && !IsMonitorUnchanged(_display, _drawable, _monitor, new CaptureRegion(_x, _y, _image.Width, _image.Height)))
            {
                // A broken connection reads no monitors either.
                if (_display.IsConnectionLost)
                    ReportError(new InvalidOperationException($"XRRGetMonitors failed{DescribeFailure(_display)}."));

                return RestartCapture();
            }

            _display.ClearError();
            bool read = _image.IsShared
                            ? X11.XShmGetImage(_display, _drawable, _image, _x, _y, X11.ALL_PLANES)
                            : X11.XGetSubImage(_display, _drawable, _x, _y, (uint)_image.Width, (uint)_image.Height, X11.ALL_PLANES, X11.ZPIXMAP, _image, 0, 0) != 0;

            // E.g. BadMatch if the screen got smaller than the image, or a broken connection. Starting over picks up the current state.
            if (!read || (_display.LastError != null))
            {
                ReportError(new InvalidOperationException($"{(_image.IsShared ? "XShmGetImage" : "XGetSubImage")} failed{DescribeFailure(_display)}."));
                return RestartCapture();
            }

            // X11 has no change detection: every successful read counts as a new frame.
            return CaptureResult.NewFrame;
        }
    }

    private static bool IsMonitorUnchanged(X11DisplayHandle display, CULong root, CULong monitor, CaptureRegion region)
        => X11Displays.TryGetMonitorRegion(display, root, monitor, out CaptureRegion current) && (current == region);

    private static string DescribeFailure(X11DisplayHandle display)
        => display.IsConnectionLost ? ": the connection to the X server was lost"
         : display.LastError is { } error ? $" with {error}"
         : string.Empty;

    /// <inheritdoc />
    protected override bool PerformCaptureZoneUpdate(CaptureZone<ColorBGRA> captureZone, Span<ColorBGRA> buffer)
    {
        ArgumentNullException.ThrowIfNull(captureZone);

        // Another thread can capture, restart or dispose meanwhile: without the lock it could overwrite or free the image while it's copied.
        lock (_captureLock)
        {
            X11ImageHandle? image = _image;
            if ((image == null) || !captureZone.Region.IsWithin(image.Width, image.Height)) return false;

            CaptureZoneWriter.Write(RefImage<ColorBGRA>.Wrap(image.Data, image.Width, image.Height, image.Stride), captureZone, buffer);

            // On depth-24 visuals the fourth byte is padding: servers fill it as they like (e.g. Xvfb with 0) and XGetSubImage clears it. The screen is opaque.
            OpaquePixels.SetOpaque(buffer);

            return true;
        }
    }

    /// <inheritdoc />
    protected override bool PerformRestart()
    {
        lock (_captureLock)
        {
            // Dispose releases everything under this lock after setting the flag: nothing created here may outlive it.
            if (IsDisposed) return false;

            DisposeHandles();

            // On failure the capture stays without handles: CaptureScreen returns Unavailable and restarts with the next attempt.
            X11DisplayHandle? display = null;
            X11ImageHandle? image = null;
            try
            {
                display = X11DisplayHandle.Open(_displayName);
                if (display.IsInvalid) throw X11DisplayHandle.CreateOpenFailedException(_displayName);

                // Read from the new connection: the display might have another position or size by now (e.g. after an XRandR mode change).
                X11Display found = X11Displays.Find(display, Display.Id) ?? throw new InvalidOperationException($"The display '{Display.Id}' isn't connected.");
                Display current = found.Display;

                nint screen = X11.XScreenOfDisplay(display, found.Screen);
                CULong drawable = X11.XRootWindowOfScreen(screen);

                // Zones are only written after a capture read a frame (a restart isn't one), so the shared image is read by the first capture.
                image = Options.UseSharedMemory ? X11ImageHandle.CreateShared(display, screen, current.Width, current.Height) : null;
                if (image == null)
                {
                    image = X11.XGetImage(display, drawable, current.X, current.Y, (uint)current.Width, (uint)current.Height, X11.ALL_PLANES, X11.ZPIXMAP);
                    if (image.IsInvalid || (display.LastError != null))
                        throw new InvalidOperationException($"XGetImage of the {current.Width}x{current.Height} display at {current.X}, {current.Y} failed{DescribeFailure(display)}.");

                    image.ReadDescription();
                }

                if (!image.IsBGRA) throw new NotSupportedException($"The X server uses an unsupported pixel format ({image.Format}). Only 32 bits per pixel with 8-bit BGR channels is supported.");

                _display = display;
                _drawable = drawable;
                _image = image;
                _x = current.X;
                _y = current.Y;
                _monitor = found.Monitor;
                display = null;
                image = null;

                SetDisplay(current);

                return true;
            }
            catch (Exception ex)
            {
                ReportError(ex);

                return false;
            }
            finally
            {
                image?.Dispose();
                display?.Dispose();
            }
        }
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        lock (_captureLock)
            DisposeHandles();

        base.Dispose(disposing);
    }

    private void DisposeHandles()
    {
        _image?.Dispose();
        _image = null;

        _display?.Dispose();
        _display = null;
        _drawable = default;
        _monitor = default;
    }

    #endregion
}