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
/// The compositor sends a frame whenever the monitor shows something new, and none while nothing changes.
/// Every frame is a <see cref="CaptureResult.NewFrame"/>, no frame within <see cref="Timeout"/> is a <see cref="CaptureResult.Timeout"/>.
/// The zones are read straight from the PipeWire buffer.
/// </para>
/// <para>
/// <see cref="IScreenCapture.Display"/> has the size of the frames, which can differ from the display <see cref="WaylandScreenCaptureService.GetDisplays"/> returned.
/// A stream that changes its size changes the display (see <see cref="IScreenCapture.DisplayChanged"/>).
/// The pixels of the zones are opaque for BGRx, BGRA is passed on as it is.
/// </para>
/// <para>
/// Known limitation: frames are taken in the orientation the compositor sends them. KDE and GNOME send rotated monitors rotated,
/// xdg-desktop-portal-wlr (e.g. sway) sends them unrotated with the rotation in the metadata, so they're captured sideways.
/// </para>
/// <para>
/// Once the screen cast session ended (e.g. the user stopped sharing), the capture is <see cref="CaptureResult.Unavailable"/>
/// until <see cref="WaylandScreenCaptureService.GetDisplays"/> started a new session sharing its display. Captures never show the dialog.
/// </para>
/// <para>
/// A (re)start waits up to 2 s for PipeWire to stream the monitor. A stream paused for more than 2 s counts as failed (a removed monitor only pauses it).
/// </para>
/// </remarks>
public sealed class WaylandScreenCapture : AbstractScreenCapture<ColorBGRA>
{
    #region Properties & Fields

    // Taken before the locks of the stream and its core.
    private readonly Lock _captureLock = new();

    private readonly WaylandScreenCaptureService _service;

    // Only used under _captureLock.
    private PipeWireStream? _stream;
    private ScreenCastSession? _session;

    /// <summary>
    /// Gets the options this capture was created with.
    /// </summary>
    public WaylandCaptureOptions Options { get; }

    // In ms, an int so it's atomic.
    private int _timeout;

    /// <summary>
    /// Gets or sets how long <see cref="IScreenCapture.CaptureScreen"/> waits for a new frame before it returns <see cref="CaptureResult.Timeout"/>.
    /// </summary>
    /// <remarks>
    /// Starts with <see cref="WaylandCaptureOptions.Timeout"/> and can be changed at any time. Rounded up to whole milliseconds; 0 doesn't wait.
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

            // The buffer was removed meanwhile (e.g. a new format).
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

        // A concurrent restart or dispose could destroy the stream while it's read.
        lock (_captureLock)
        {
            if (_stream == null) return false;

            bool hasAlpha;

            // Removing the buffer (e.g. on a new format) waits for this lock.
            lock (_stream.BufferLock)
            {
                if ((_stream.CurrentFrame is not { } frame) || !captureZone.Region.IsWithin(frame.Width, frame.Height)) return false;

                ReadOnlySpan<byte> pixels = new((void*)frame.Data, (frame.Stride * (frame.Height - 1)) + (frame.Width * 4));
                CaptureZoneWriter.Write(RefImage<ColorBGRA>.Wrap(pixels, frame.Width, frame.Height, frame.Stride), captureZone, buffer);
                hasAlpha = frame.HasAlpha;
            }

            // In BGRx the fourth byte is padding, compositors fill it as they like.
            if (!hasAlpha) OpaquePixels.SetOpaque(buffer);

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

            DisposeStream();

            try
            {
                // In the current session, which can have replaced the old one.
                (ScreenCastSession session, PortalStream portalStream, Display display) = _service.FindStream(Display.Id)
                    ?? throw new InvalidOperationException($"The display '{Display.Id}' isn't shared (anymore): call GetDisplays of the service to start a new screen cast session.");

                _stream = session.GetPipeWireCore().CreateStream(portalStream.NodeId, display.Width, display.Height);
                _session = session;

                // E.g. the node is gone.
                if (_stream.WaitUntilStreaming() is { } failure) throw failure;

                // The portal's size can be missing or differ (e.g. the logical size on a scaled monitor).
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
