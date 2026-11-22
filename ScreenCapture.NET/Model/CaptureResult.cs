namespace ScreenCapture.NET;

/// <summary>
/// Describes the outcome of <see cref="IScreenCapture.CaptureScreen"/>.
/// </summary>
public enum CaptureResult
{
    /// <summary>
    /// A new frame was captured.
    /// </summary>
    NewFrame = 0,

    /// <summary>
    /// The capture works, but there's no new frame (e.g. DX11 when only the mouse cursor moved). The last frame is still current.
    /// </summary>
    NoChange,

    /// <summary>
    /// No new frame arrived within the timeout (DX11, WGC and Wayland). The last frame is still current.
    /// </summary>
    Timeout,

    /// <summary>
    /// The capture lost its source (e.g. a display mode change) and restarted. The next call captures again.
    /// The cause is reported with <see cref="IScreenCapture.LastError"/>.
    /// </summary>
    Restarting,

    /// <summary>
    /// The capture can't run right now (e.g. a UAC prompt or the lock screen is shown, or the X server can't be reached).
    /// The next call tries to restart it after the backoff (see <see cref="IScreenCapture.CaptureScreen"/>). <see cref="IScreenCapture.LastError"/> tells why.
    /// </summary>
    Unavailable,

    /// <summary>
    /// An unexpected error stopped the capture (see <see cref="IScreenCapture.LastError"/>).
    /// </summary>
    Failed
}