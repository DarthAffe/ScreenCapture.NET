using System;
using System.Diagnostics;
using System.Threading;
using HPPH;

namespace ScreenCapture.NET;

/// <summary>
/// Represents a ScreenCapture of a monitor shared through the ScreenCast portal, receiving its frames from PipeWire.
/// https://flatpak.github.io/xdg-desktop-portal/docs/doc-org.freedesktop.portal.ScreenCast.html
/// </summary>
/// <remarks>
/// <para>
/// The compositor sends a frame whenever the monitor shows something new (up to its refresh rate), and none while nothing changes:
/// every frame is a <see cref="CaptureResult.NewFrame"/> (also one that looks like the previous), no frame within <see cref="Timeout"/> is a <see cref="CaptureResult.Timeout"/>.
/// The zones are read from the buffer of the frame without copying it first.
/// The timestamp of a frame (<see cref="ScreenCaptureUpdatedEventArgs.Timestamp"/>) is when the compositor produced it, if it tells.
/// </para>
/// <para>
/// The frames have the size the compositor sends, and so does <see cref="IScreenCapture.Display"/>: it can differ from the display <see cref="WaylandScreenCaptureService.GetDisplays"/> returned
/// (e.g. the portal didn't tell the size). A stream that changes its size changes the display (see <see cref="IScreenCapture.DisplayChanged"/>).
/// The pixels of the zones are opaque if the compositor sends BGRx (its fourth byte is padding); BGRA is passed on as it is.
/// </para>
/// <para>
/// Known limitation: frames are taken in the orientation the compositor sends them. KDE and GNOME send rotated monitors already rotated.
/// xdg-desktop-portal-wlr (wlroots desktops, e.g. sway) sends them in their unrotated orientation and only describes the rotation in the metadata of the frame,
/// which the capture doesn't apply: a rotated monitor is captured sideways, with the unrotated size.
/// </para>
/// <para>
/// The capture belongs to the screen cast session of its <see cref="WaylandScreenCaptureService"/>: once the session ended (the user stopped sharing, or the connection to PipeWire broke)
/// it's <see cref="CaptureResult.Unavailable"/>, until <see cref="WaylandScreenCaptureService.GetDisplays"/> started a new session that shares its display again
/// (captures never show the dialog themselves).
/// </para>
/// <para>
/// A (re)start waits until PipeWire streams the monitor, up to 2 s: it fails if the monitor isn't shared anymore or offers no supported format (see <see cref="IScreenCapture.LastError"/>).
/// A stream that pauses for more than 2 s (PipeWire pauses it when the compositor removes the monitor) counts as failed: the capture restarts, which fails if the monitor is gone.
/// </para>
/// </remarks>
public sealed class WaylandScreenCapture : AbstractScreenCapture<ColorBGRA>
{
    #region Properties & Fields

    // Guards the stream: held by captures, zone updates and restarts. Acquired before the locks of the stream and its core.
    private readonly Lock _captureLock = new();

    private readonly WaylandScreenCaptureService _service;

    // Only used under _captureLock.
    private PipeWireStream? _stream;
    private ScreenCastSession? _session;

    /// <summary>
    /// Gets the options this capture was created with.
    /// </summary>
    public WaylandCaptureOptions Options { get; }

    // In whole milliseconds; an int, so it's read and written atomically.
    private int _timeout;

    /// <summary>
    /// Gets or sets how long <see cref="IScreenCapture.CaptureScreen"/> waits for a new frame before it returns <see cref="CaptureResult.Timeout"/>.
    /// </summary>
    /// <remarks>
    /// It starts with <see cref="WaylandCaptureOptions.Timeout"/> and can be changed at any time; a running capture keeps the value it started with.
    /// It's rounded up to whole milliseconds; 0 doesn't wait.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative (including <see cref="System.Threading.Timeout.InfiniteTimeSpan"/>) or greater than <see cref="WaylandCaptureOptions.MaxTimeout"/>.</exception>
    public TimeSpan Timeout
    {
        get => TimeSpan.FromMilliseconds(_timeout);
        set => _timeout = CaptureTimeout.ToMilliseconds(CaptureTimeout.Validate(value));
    }

    #endregion

    #region Constructors

    internal WaylandScreenCapture(WaylandScreenCaptureService service, Display display, WaylandCaptureOptions options)
        : base(display)
    {
        this._service = service;
        this.Options = options;
        this._timeout = CaptureTimeout.ToMilliseconds(options.Timeout);

        Restart();
    }

    #endregion

    #region Methods

    /// <inheritdoc />
    protected override CaptureResult PerformScreenCapture(out long timestamp)
    {
        timestamp = 0;

        lock (_captureLock)
        {
            if ((_stream == null) || (_session == null))
                return RestartCapture();

            if (_session.IsClosed)
            {
                ReportError(new InvalidOperationException("The screen cast session ended."));
                return RestartCapture();
            }

            long deadline = Stopwatch.GetTimestamp() + ((_timeout * Stopwatch.Frequency) / 1000);
            if (!_stream.WaitForFrame(deadline))
            {
                if (_stream.Failure is not { } failure) return CaptureResult.Timeout;

                ReportError(failure);
                return RestartCapture();
            }

            // The buffer of the frame was removed meanwhile (e.g. a new format): the next one comes with the new buffers.
            if (_stream.TakeNewestFrame() is not { } frame) return CaptureResult.Timeout;

            Display display = Display;
            if ((frame.Width != display.Width) || (frame.Height != display.Height))
                SetDisplay(display with { Width = frame.Width, Height = frame.Height });

            timestamp = frame.Timestamp;
            return CaptureResult.NewFrame;
        }
    }

    /// <inheritdoc />
    protected override unsafe bool PerformCaptureZoneUpdate(CaptureZone<ColorBGRA> captureZone, Span<ColorBGRA> buffer)
    {
        ArgumentNullException.ThrowIfNull(captureZone);

        // Another thread can capture, restart or dispose meanwhile: without the lock the stream could be destroyed while it's read.
        lock (_captureLock)
        {
            if (_stream == null) return false;

            bool hasAlpha;

            // PipeWire can remove the buffer (e.g. on a new format) while the zone is written: it waits for this lock.
            lock (_stream.BufferLock)
            {
                if ((_stream.CurrentFrame is not { } frame) || !captureZone.Region.IsWithin(frame.Width, frame.Height)) return false;

                ReadOnlySpan<byte> pixels = new((void*)frame.Data, (frame.Stride * (frame.Height - 1)) + (frame.Width * 4));
                CaptureZoneWriter.Write(RefImage<ColorBGRA>.Wrap(pixels, frame.Width, frame.Height, frame.Stride), captureZone, buffer);
                hasAlpha = frame.HasAlpha;
            }

            // BGRx: the fourth byte is padding, compositors fill it as they like. The screen is opaque.
            if (!hasAlpha) OpaquePixels.SetOpaque(buffer);

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

            DisposeStream();

            try
            {
                // The session can have ended and been replaced by a new one (GetDisplays): the display is looked up in the current one.
                (ScreenCastSession session, PortalStream portalStream, Display display) = _service.FindStream(Display.Id)
                    ?? throw new InvalidOperationException($"The display '{Display.Id}' isn't shared (anymore): call GetDisplays of the service to start a new screen cast session.");

                _stream = session.GetPipeWireCore().CreateStream(portalStream.NodeId, display.Width, display.Height);
                _session = session;

                // E.g. the node is gone (the compositor stopped sharing the monitor): PipeWire reports it while the stream connects.
                if (_stream.WaitUntilStreaming() is { } failure) throw failure;

                // The size the portal reported can differ from the frames (e.g. the logical size on a scaled monitor) or be missing; the negotiated one is the size of the frames.
                (int width, int height) = _stream.FrameSize;
                SetDisplay(display with { Width = width, Height = height });

                return true;
            }
            catch (Exception ex)
            {
                DisposeStream();
                ReportError(ex);

                return false;
            }
        }
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        lock (_captureLock)
            DisposeStream();

        base.Dispose(disposing);
    }

    private void DisposeStream()
    {
        _stream?.Dispose();
        _stream = null;
        _session = null;
    }

    #endregion
}
