using System;

namespace ScreenCapture.NET;

/// <summary>
/// The frame timeout of the captures that wait for new frames (DX11, WGC, Wayland): validated as a <see cref="TimeSpan"/>, waited for in whole milliseconds.
/// </summary>
internal static class CaptureTimeout
{
    #region Properties & Fields

    /// <summary>
    /// Gets the longest supported timeout (<see cref="int.MaxValue"/> milliseconds, about 24.8 days).
    /// </summary>
    public static TimeSpan Max { get; } = TimeSpan.FromMilliseconds(int.MaxValue);

    #endregion

    #region Methods

    /// <summary>
    /// Checks that a timeout isn't negative (including <see cref="System.Threading.Timeout.InfiniteTimeSpan"/>) and not greater than <see cref="Max"/>.
    /// </summary>
    /// <param name="timeout">The timeout.</param>
    /// <returns><paramref name="timeout"/>.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The timeout is negative or greater than <see cref="Max"/> (parameter name <c>Timeout</c>, the property it's set to).</exception>
    public static TimeSpan Validate(TimeSpan timeout)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(timeout, TimeSpan.Zero, "Timeout");
        ArgumentOutOfRangeException.ThrowIfGreaterThan(timeout, Max, "Timeout");

        return timeout;
    }

    /// <summary>
    /// Converts a validated timeout to whole milliseconds, rounded up.
    /// </summary>
    public static int ToMilliseconds(TimeSpan timeout) => (int)Math.Ceiling(timeout.TotalMilliseconds);

    #endregion
}
