using System;
using System.Diagnostics;

namespace ScreenCapture.NET;

/// <summary>
/// The delay between restart attempts of a capture: none until a restart fails, then 100 ms, doubling with every further failure up to 2 s.
/// </summary>
/// <remarks>
/// Not thread-safe. Times are <see cref="Stopwatch"/> timestamps.
/// </remarks>
internal sealed class RestartBackoff
{
    #region Constants

    public static readonly TimeSpan MIN_DELAY = TimeSpan.FromMilliseconds(100);
    public static readonly TimeSpan MAX_DELAY = TimeSpan.FromSeconds(2);

    #endregion

    #region Properties & Fields

    private bool _failed;
    private TimeSpan _delay;
    private long _nextAttempt;

    #endregion

    #region Methods

    /// <summary>
    /// Records a successful restart: the next attempt (after the capture lost its source again) is due right away.
    /// </summary>
    public void Succeeded() => _failed = false;

    /// <summary>
    /// Records a failed restart: the next attempt waits for the next delay.
    /// </summary>
    /// <param name="timestamp">When the restart failed.</param>
    public void Failed(long timestamp)
    {
        _delay = _failed ? TimeSpan.FromTicks(Math.Min(_delay.Ticks * 2, MAX_DELAY.Ticks)) : MIN_DELAY;
        _failed = true;
        _nextAttempt = timestamp + (long)(_delay.TotalSeconds * Stopwatch.Frequency);
    }

    /// <summary>
    /// Gets how long the next restart attempt has to wait.
    /// </summary>
    /// <param name="timestamp">The current time.</param>
    /// <returns>The time until the next attempt is due; <see cref="TimeSpan.Zero"/> if it's due now.</returns>
    public TimeSpan GetRemaining(long timestamp)
        => (!_failed || (timestamp >= _nextAttempt)) ? TimeSpan.Zero : Stopwatch.GetElapsedTime(timestamp, _nextAttempt);

    #endregion
}