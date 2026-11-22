using System;
using System.Diagnostics;

namespace ScreenCapture.NET;

/// <summary>
/// Decides when a capture loop captures next: at a target frame rate, with a growing delay while captures keep failing.
/// </summary>
/// <remarks>
/// Not thread-safe. Times are <see cref="Stopwatch"/> timestamps.
/// <para>
/// Captures are scheduled on a fixed grid, so waking up late doesn't lower the average rate.
/// A capture a whole interval late starts a new grid, so the loop doesn't catch up in a burst.
/// </para>
/// </remarks>
internal sealed class CapturePacer
{
    #region Constants

    /// <summary>
    /// The longest interval between captures (one day). Lower frame rates are rejected.
    /// </summary>
    public static readonly TimeSpan MAX_INTERVAL = TimeSpan.FromDays(1);

    #endregion

    #region Properties & Fields

    // Same delays as failed restarts.
    private readonly Backoff _failureBackoff = new();

    private bool _hasCaptured;

    // When the last capture was due on the grid.
    private long _lastDue;

    #endregion

    #region Methods

    /// <summary>
    /// Gets the interval between captures for the given target frame rate.
    /// </summary>
    /// <param name="targetFrameRate">The target frame rate, or <c>null</c> for no limit.</param>
    /// <param name="paramName">The name of the parameter the frame rate was passed as.</param>
    /// <returns>The interval; <see cref="TimeSpan.Zero"/> for no limit.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="targetFrameRate"/> isn't a positive finite number of at least one frame per day.</exception>
    public static TimeSpan GetInterval(double? targetFrameRate, string paramName)
    {
        if (targetFrameRate is not { } frameRate) return TimeSpan.Zero;

        // Also rejects NaN.
        if (!(frameRate > 0) || double.IsPositiveInfinity(frameRate) || ((1 / frameRate) > MAX_INTERVAL.TotalSeconds))
            throw new ArgumentOutOfRangeException(paramName, targetFrameRate, "The target frame rate must be a finite number greater than 0 and at least one frame per day.");

        return TimeSpan.FromSeconds(1 / frameRate);
    }

    /// <summary>
    /// Converts a delay to whole milliseconds, rounded up.
    /// </summary>
    /// <param name="delay">The delay.</param>
    /// <returns>The milliseconds to wait.</returns>
    public static int ToWaitMilliseconds(TimeSpan delay)
        => (int)Math.Clamp(Math.Ceiling(delay.TotalMilliseconds), 0, int.MaxValue);

    /// <summary>
    /// Gets how long to wait before the next capture.
    /// </summary>
    /// <param name="timestamp">The current time.</param>
    /// <param name="interval">The interval between captures; <see cref="TimeSpan.Zero"/> for no limit.</param>
    /// <returns>The time until the next capture is due; <see cref="TimeSpan.Zero"/> if it's due now.</returns>
    public TimeSpan GetDelay(long timestamp, TimeSpan interval)
    {
        TimeSpan delay = _failureBackoff.GetRemaining(timestamp);

        if (_hasCaptured && (interval > TimeSpan.Zero))
        {
            long due = _lastDue + ToStopwatchTicks(interval);
            if (timestamp < due)
            {
                TimeSpan paced = Stopwatch.GetElapsedTime(timestamp, due);
                if (paced > delay)
                    delay = paced;
            }
        }

        return delay;
    }

    /// <summary>
    /// Records a capture.
    /// </summary>
    /// <param name="started">When the capture started.</param>
    /// <param name="ended">When the capture returned.</param>
    /// <param name="result">The result of the capture.</param>
    /// <param name="interval">The interval between captures the capture was paced with; <see cref="TimeSpan.Zero"/> for no limit.</param>
    public void Captured(long started, long ended, CaptureResult result, TimeSpan interval)
    {
        long intervalTicks = ToStopwatchTicks(interval);
        long due = _hasCaptured ? _lastDue + intervalTicks : started;

        // Early (after the interval was shortened) or a whole interval late: start a new grid.
        _lastDue = ((started < due) || ((started - due) >= intervalTicks)) ? started : due;
        _hasCaptured = true;

        if (result == CaptureResult.Failed)
            _failureBackoff.Failed(ended);
        else
            _failureBackoff.Succeeded();
    }

    private static long ToStopwatchTicks(TimeSpan interval) => (long)(interval.TotalSeconds * Stopwatch.Frequency);

    #endregion
}
