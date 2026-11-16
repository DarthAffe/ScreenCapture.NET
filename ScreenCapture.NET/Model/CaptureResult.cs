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
    /// The capture works, but the display shows no new frame (DX11: e.g. only the mouse cursor moved). The last frame is still current.
    /// </summary>
    NoChange,

    /// <summary>
    /// No new frame arrived within the timeout of the capture (DX11, see <c>DX11ScreenCapture.Timeout</c>). The last frame is still current.
    /// </summary>
    Timeout,

    /// <summary>
    /// The capture lost its source (e.g. a lost device, a display mode change, or an X server error) and restarted. The next call captures again.
    /// </summary>
    Restarting,

    /// <summary>
    /// The capture can't run right now (e.g. the secure desktop of a UAC prompt or the lock screen is shown, or the X server can't be reached).
    /// It tries to restart on the next call.
    /// </summary>
    Unavailable,

    /// <summary>
    /// An unexpected error stopped the capture.
    /// </summary>
    Failed
}