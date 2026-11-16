using System.Diagnostics;

namespace ScreenCapture.NET.Tests;

/// <summary>
/// The delay sequence, with explicit timestamps (see <see cref="CaptureRestartTests"/> for how captures use it).
/// </summary>
[TestClass]
public sealed class RestartBackoffTests
{
    // Far from 0, like a real Stopwatch timestamp.
    private static readonly long START = Stopwatch.Frequency * 1000;

    [TestMethod]
    public void NoWaitBeforeAFailure()
    {
        RestartBackoff backoff = new();

        AssertRemaining(0, backoff.GetRemaining(START));
    }

    [TestMethod]
    public void FirstFailureWaitsTheShortestDelay()
    {
        RestartBackoff backoff = new();
        backoff.Failed(At(0));

        AssertRemaining(100, backoff.GetRemaining(At(0)));
        AssertRemaining(60, backoff.GetRemaining(At(40)));
        AssertRemaining(0, backoff.GetRemaining(At(100)));
        AssertRemaining(0, backoff.GetRemaining(At(150)));
    }

    [TestMethod]
    public void DelayDoublesUpToTheMaximum()
    {
        RestartBackoff backoff = new();

        List<double> delays = [];
        double now = 0;
        for (int i = 0; i < 8; i++)
        {
            backoff.Failed(At(now));
            double delay = backoff.GetRemaining(At(now)).TotalMilliseconds;
            delays.Add(Math.Round(delay));
            now += delay;
        }

        CollectionAssert.AreEqual(new double[] { 100, 200, 400, 800, 1600, 2000, 2000, 2000 }, delays);
    }

    [TestMethod]
    public void FailureCountsFromWhenItHappened()
    {
        RestartBackoff backoff = new();
        backoff.Failed(At(0));

        // A late attempt (e.g. a Restart call long after) still waits the full next delay.
        backoff.Failed(At(5000));

        AssertRemaining(200, backoff.GetRemaining(At(5000)));
    }

    [TestMethod]
    public void SuccessResetsTheDelay()
    {
        RestartBackoff backoff = new();
        for (int i = 0; i < 4; i++)
            backoff.Failed(At(0));

        backoff.Succeeded();
        AssertRemaining(0, backoff.GetRemaining(At(0)));

        backoff.Failed(At(0));
        AssertRemaining(100, backoff.GetRemaining(At(0)));
    }

    private static long At(double milliseconds) => START + (long)(milliseconds / 1000 * Stopwatch.Frequency);

    // Stopwatch ticks and TimeSpan ticks can differ, so conversions may be off by a tick.
    private static void AssertRemaining(double expectedMilliseconds, TimeSpan remaining)
        => Assert.AreEqual(expectedMilliseconds, remaining.TotalMilliseconds, 0.001);
}
