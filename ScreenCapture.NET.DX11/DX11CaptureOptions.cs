using System;

namespace ScreenCapture.NET;

/// <summary>
/// Options of a <see cref="DX11ScreenCapture"/>.
/// </summary>
/// <remarks>
/// Passed to the <see cref="DX11ScreenCaptureService"/> for all its captures, or to <see cref="DX11ScreenCaptureService.GetScreenCapture(Display, DX11CaptureOptions)"/> for one capture.
/// Two options are equal if all their values are.
/// </remarks>
public sealed record DX11CaptureOptions
{
    #region Properties & Fields

    /// <summary>
    /// Gets the longest supported <see cref="Timeout"/> (<see cref="int.MaxValue"/> milliseconds, about 24.8 days).
    /// </summary>
    public static TimeSpan MaxTimeout { get; } = TimeSpan.FromMilliseconds(int.MaxValue);

    /// <summary>
    /// Gets the default options.
    /// </summary>
    public static DX11CaptureOptions Default { get; } = new();

    /// <summary>
    /// Gets how long <see cref="IScreenCapture.CaptureScreen"/> waits for a new frame before it returns <see cref="CaptureResult.Timeout"/>. (default 1 s)
    /// </summary>
    /// <remarks>
    /// It's rounded up to whole milliseconds; 0 doesn't wait. Disposing a capture waits for a running <see cref="IScreenCapture.CaptureScreen"/>, so up to this long.
    /// The <see cref="DX11ScreenCapture.Timeout"/> of a capture starts with this value and can be changed while it runs.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative (including <see cref="System.Threading.Timeout.InfiniteTimeSpan"/>) or greater than <see cref="MaxTimeout"/>.</exception>
    public TimeSpan Timeout
    {
        get;
        init => field = ValidateTimeout(value);
    } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Gets a value indicating whether the capture uses <c>IDXGIOutput5::DuplicateOutput1</c> instead of <c>IDXGIOutput1::DuplicateOutput</c>. (default <c>false</c>)
    /// </summary>
    /// <remarks>
    /// <c>DuplicateOutput1</c> needs a per-monitor DPI aware (version 2) process (see <see cref="DPIAwareness.Initialize"/>) and doesn't work on a WPF thread.
    /// If it fails, the capture reports the error (<see cref="IScreenCapture.LastError"/>) and returns <see cref="CaptureResult.Unavailable"/>.
    /// It currently captures the same 8-bit image as <c>DuplicateOutput</c>; it's the base for HDR capturing.
    /// </remarks>
    public bool UseDuplicateOutput1 { get; init; }

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
