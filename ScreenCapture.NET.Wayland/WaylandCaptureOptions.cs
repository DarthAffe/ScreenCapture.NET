using System;

namespace ScreenCapture.NET;

/// <summary>
/// Options of a <see cref="WaylandScreenCapture"/>.
/// </summary>
/// <remarks>
/// Passed to the <see cref="WaylandScreenCaptureService"/> for all its captures, or to <see cref="WaylandScreenCaptureService.GetScreenCapture(Display, WaylandCaptureOptions)"/> for one capture.
/// Two options are equal if all their values are.
/// </remarks>
public sealed record WaylandCaptureOptions
{
    #region Properties & Fields

    /// <summary>
    /// Gets the longest supported <see cref="Timeout"/> (<see cref="int.MaxValue"/> milliseconds, about 24.8 days).
    /// </summary>
    public static TimeSpan MaxTimeout => CaptureTimeout.Max;

    /// <summary>
    /// Gets the default options.
    /// </summary>
    public static WaylandCaptureOptions Default { get; } = new();

    /// <summary>
    /// Gets how long <see cref="IScreenCapture.CaptureScreen"/> waits for a new frame before it returns <see cref="CaptureResult.Timeout"/>. (default 1 s)
    /// </summary>
    /// <remarks>
    /// The compositor only sends a frame when the monitor shows something new. It's rounded up to whole milliseconds; 0 doesn't wait.
    /// Disposing a capture waits for a running <see cref="IScreenCapture.CaptureScreen"/>, so up to this long.
    /// The <see cref="WaylandScreenCapture.Timeout"/> of a capture starts with this value and can be changed while it runs.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative (including <see cref="System.Threading.Timeout.InfiniteTimeSpan"/>) or greater than <see cref="MaxTimeout"/>.</exception>
    public TimeSpan Timeout
    {
        get;
        init => field = CaptureTimeout.Validate(value);
    } = TimeSpan.FromSeconds(1);

    #endregion
}
