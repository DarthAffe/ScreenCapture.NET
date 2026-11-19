using System;

namespace ScreenCapture.NET;

/// <summary>
/// Options of a <see cref="WGCScreenCapture"/>.
/// </summary>
/// <remarks>
/// Passed to the <see cref="WGCScreenCaptureService"/> for all its captures, or to <see cref="WGCScreenCaptureService.GetScreenCapture(Display, WGCCaptureOptions)"/> for one capture.
/// Two options are equal if all their values are.
/// </remarks>
// ReSharper disable once InconsistentNaming
public sealed record WGCCaptureOptions
{
    #region Properties & Fields

    /// <summary>
    /// Gets the longest supported <see cref="Timeout"/> (<see cref="int.MaxValue"/> milliseconds, about 24.8 days).
    /// </summary>
    public static TimeSpan MaxTimeout { get; } = TimeSpan.FromMilliseconds(int.MaxValue);

    /// <summary>
    /// Gets the default options.
    /// </summary>
    public static WGCCaptureOptions Default { get; } = new();

    /// <summary>
    /// Gets how long <see cref="IScreenCapture.CaptureScreen"/> waits for a new frame before it returns <see cref="CaptureResult.Timeout"/>. (default 1 s)
    /// </summary>
    /// <remarks>
    /// It's rounded up to whole milliseconds; 0 doesn't wait. Disposing a capture waits for a running <see cref="IScreenCapture.CaptureScreen"/>, so up to this long.
    /// The <see cref="WGCScreenCapture.Timeout"/> of a capture starts with this value and can be changed while it runs.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative (including <see cref="System.Threading.Timeout.InfiniteTimeSpan"/>) or greater than <see cref="MaxTimeout"/>.</exception>
    public TimeSpan Timeout
    {
        get;
        init => field = ValidateTimeout(value);
    } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Gets if Windows draws a border around the captured display. (default <c>false</c>)
    /// </summary>
    /// <remarks>
    /// The border isn't part of the frames of this capture, but other captures (like DX11 or DX9) and the user see it.
    /// Windows 10 always draws it; turning it off needs Windows 11.
    /// </remarks>
    public bool ShowBorder { get; init; }

    #endregion

    #region Methods

    internal static TimeSpan ValidateTimeout(TimeSpan timeout)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(timeout, TimeSpan.Zero, nameof(Timeout));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(timeout, MaxTimeout, nameof(Timeout));

        return timeout;
    }

    /// <summary>
    /// Converts a validated timeout to whole milliseconds, rounded up.
    /// </summary>
    internal static int ToMilliseconds(TimeSpan timeout) => (int)Math.Ceiling(timeout.TotalMilliseconds);

    #endregion
}
