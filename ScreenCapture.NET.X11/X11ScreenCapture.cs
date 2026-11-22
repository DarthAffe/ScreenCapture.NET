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
/// The first connection installs process-wide Xlib error handlers (<c>XSetErrorHandler</c>, and <c>XSetIOErrorHandler</c> with libX11 1.7+), so Xlib doesn't terminate the process.
/// Errors of other connections are passed to the handlers installed before. Handlers installed later that don't pass errors on take over the errors of the capture.
/// <para>
/// A failed request or a broken connection makes the capture restart; while the X server can't be reached it's <see cref="CaptureResult.Unavailable"/>.
/// With libX11 older than 1.7, Xlib still terminates the process when a connection breaks.
/// A connection that stops responding without breaking blocks the capture until the system gives up on it, which can take minutes.
/// </para>
/// <para>
/// A display is a RandR monitor, and only its rectangle of the X screen is read. Every capture checks the monitor (one more round trip):
/// a moved or resized monitor makes the capture restart (see <see cref="IScreenCapture.DisplayChanged"/>), a removed one makes it unavailable until it's back.
/// </para>
/// <para>
/// A whole X screen (a server without RandR monitors) is only read again by a restart. A screen that got smaller restarts on its own,
/// one that got larger keeps its size until <see cref="IScreenCapture.Restart"/> is called.
/// </para>
/// <para>
/// The pixels of the zones are always opaque.
/// </para>
/// </remarks>
// ReSharper disable once InconsistentNaming
public sealed class X11ScreenCapture : AbstractScreenCapture<ColorBGRA>
{
    #region Properties & Fields

    private readonly Lock _captureLock = new();

    // The fields below are only used under _captureLock.
    private X11DisplayHandle? _display;
    private CULong _drawable;

    private X11ImageHandle? _image;

    // A shared image is empty until the first capture after a restart.
    private bool _hasFrame;

    // Where the display is on its X screen, and its RandR monitor (0 for a whole screen).
    private int _x;
    private int _y;
    private CULong _monitor;

    // null for $DISPLAY.
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
        // X11 doesn't tell when the frame was presented.
        timestamp = 0;

        lock (_captureLock)
        {
            if ((_display == null) || (_image == null))
                return RestartCapture();

            // Reading a moved, resized or removed monitor would succeed with the wrong pixels.
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

            // E.g. BadMatch if the screen got smaller, or a broken connection.
            if (!read || (_display.LastError != null))
            {
                ReportError(new InvalidOperationException($"{(_image.IsShared ? "XShmGetImage" : "XGetSubImage")} failed{DescribeFailure(_display)}."));
                return RestartCapture();
            }

            _hasFrame = true;

            // No change detection: every read counts as a new frame.
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

        // A concurrent capture, restart or dispose could overwrite or free the image while it's copied.
        lock (_captureLock)
        {
            // A restart on another thread can have replaced the image.
            X11ImageHandle? image = _image;
            if (!_hasFrame || (image == null) || !captureZone.Region.IsWithin(image.Width, image.Height)) return false;

            CaptureZoneWriter.Write(RefImage<ColorBGRA>.Wrap(image.Data, image.Width, image.Height, image.Stride), captureZone, buffer);

            // On depth-24 visuals the fourth byte is padding, servers fill it as they like.
            OpaquePixels.SetOpaque(buffer);

            return true;
        }
    }

    /// <inheritdoc />
    protected override bool PerformRestart()
    {
        lock (_captureLock)
        {
            // Nothing created here may outlive Dispose.
            if (IsDisposed) return false;

            DisposeHandles();

            X11DisplayHandle? display = null;
            X11ImageHandle? image = null;
            try
            {
                display = X11DisplayHandle.Open(_displayName);
                if (display.IsInvalid) throw X11DisplayHandle.CreateOpenFailedException(_displayName);

                X11Display found = X11Displays.Find(display, Display.Id) ?? throw new InvalidOperationException($"The display '{Display.Id}' isn't connected.");
                Display current = found.Display;

                nint screen = X11.XScreenOfDisplay(display, found.Screen);
                CULong drawable = X11.XRootWindowOfScreen(screen);

                // A shared image stays empty until the first capture.
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
        _hasFrame = false;

        _display?.Dispose();
        _display = null;
        _drawable = default;
        _monitor = default;
    }

    #endregion
}