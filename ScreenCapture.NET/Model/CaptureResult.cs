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
    /// The capture works, but there's no new frame to show (DX11: e.g. only the mouse cursor moved; WGC: a frame from before a window was resized). The last frame is still current.
    /// </summary>
    NoChange,

    /// <summary>
    /// No new frame arrived within the timeout of a capture that waits for frames (DX11, WGC and Wayland, see their <c>Timeout</c>). The last frame is still current.
    /// </summary>
    Timeout,

    /// <summary>
    /// The capture lost its source (e.g. a lost device, a display mode change, or an X server error) and restarted. The next call captures again.
    /// The error that caused the restart is reported with <see cref="IScreenCapture.LastError"/>.
    /// </summary>
    Restarting,

    /// <summary>
    /// The capture can't run right now (e.g. the secure desktop of a UAC prompt or the lock screen is shown, or the X server can't be reached).
    /// The next call tries to restart it, after a delay that grows from 100 ms to 2 s while restarts keep failing (see <see cref="IScreenCapture.CaptureScreen"/>).
    /// <see cref="IScreenCapture.LastError"/> tells why the last restart failed.
    /// </summary>
    Unavailable,

    /// <summary>
    /// An unexpected error stopped the capture (see <see cref="IScreenCapture.LastError"/>).
    /// </summary>
    Failed
}