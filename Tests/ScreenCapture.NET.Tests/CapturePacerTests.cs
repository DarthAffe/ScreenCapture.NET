using System.Diagnostics;

namespace ScreenCapture.NET.Tests;

/// <summary>
/// When capture loops capture, with explicit timestamps (see <see cref="ScreenCaptureLoopTests"/> and <see cref="CaptureLoopAsyncTests"/> for the loops using it).
/// </summary>
[TestClass]
public sealed class CapturePacerTests
{
    // Far from 0, like a real Stopwatch timestamp.
    private static readonly long START = Stopwatch.Frequency * 1000;

    private static readonly TimeSpan INTERVAL = TimeSpan.FromMilliseconds(20);

    [TestMethod]
    [DataRow(null, 0.0)]
    [DataRow(50.0, 20.0)]
    [DataRow(0.5, 2000.0)]
    [DataRow(1.0 / 86400, 86400000.0)]
    public void FrameRateGivesTheInterval(double? frameRate, double expectedMilliseconds)
        => Assert.AreEqual(expectedMilliseconds, CapturePacer.GetInterval(frameRate, "frameRate").TotalMilliseconds, 0.001);

    [TestMethod]
    [DataRow(0.0)]
    [DataRow(-1.0)]
    [DataRow(double.NaN)]
    [DataRow(double.PositiveInfinity)]
    [DataRow(double.NegativeInfinity)]
    [DataRow(1.0 / 86401)]
    public void InvalidFrameRateThrows(double frameRate)
    {
        ArgumentOutOfRangeException exception = Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => CapturePacer.GetInterval(frameRate, "frameRate"));
        Assert.AreEqual("frameRate", exception.ParamName);
    }

    [TestMethod]
    [DataRow(0.0, 0)]
    [DataRow(0.0001, 1)]
    [DataRow(16.2, 17)]
    [DataRow(20.0, 20)]
    public void WaitsAreRoundedUp(double milliseconds, int expected)
        => Assert.AreEqual(expected, CapturePacer.ToWaitMilliseconds(TimeSpan.FromMilliseconds(milliseconds)));

    [TestMethod]
    public void FirstCaptureIsDueRightAway()
    {
        CapturePacer pacer = new();

        AssertDelay(0, pacer.GetDelay(At(0), INTERVAL));
    }

    [TestMethod]
    public void CapturesFollowTheInterval()
    {
        CapturePacer pacer = new();

        Capture(pacer, 0, 5);
        AssertDelay(15, pacer.GetDelay(At(5), INTERVAL));
        AssertDelay(0, pacer.GetDelay(At(20), INTERVAL));

        Capture(pacer, 20, 21);
        AssertDelay(19, pacer.GetDelay(At(21), INTERVAL));
    }

    [TestMethod]
    public void LateCapturesKeepTheAverageRate()
    {
        CapturePacer pacer = new();
        Capture(pacer, 0, 1);

        // Woke up 15 ms late (coarse OS timer): the next capture is still due at 40 ms.
        Capture(pacer, 35, 36);
        AssertDelay(4, pacer.GetDelay(At(36), INTERVAL));
    }

    [TestMethod]
    public void FallingBehindStartsANewGrid()
    {
        CapturePacer pacer = new();
        Capture(pacer, 0, 1);

        // Started a whole interval late (due at 20 ms): no burst to catch up, the next capture is an interval after this one.
        Capture(pacer, 40, 41);
        AssertDelay(19, pacer.GetDelay(At(41), INTERVAL));
    }

    [TestMethod]
    public void LongCaptureIsFollowedRightAway()
    {
        CapturePacer pacer = new();

        // E.g. DX11 waited for a new frame: that wait was the pacing.
        Capture(pacer, 0, 1000, CaptureResult.Timeout);
        AssertDelay(0, pacer.GetDelay(At(1000), INTERVAL));
    }

    [TestMethod]
    public void NoLimitNeverWaits()
    {
        CapturePacer pacer = new();

        for (int i = 0; i < 3; i++)
        {
            Capture(pacer, i, i, interval: TimeSpan.Zero);
            AssertDelay(0, pacer.GetDelay(At(i), TimeSpan.Zero));
        }
    }

    [TestMethod]
    public void ChangedIntervalAppliesToTheNextCapture()
    {
        CapturePacer pacer = new();
        Capture(pacer, 0, 1);

        AssertDelay(99, pacer.GetDelay(At(1), TimeSpan.FromMilliseconds(100)));
        AssertDelay(4, pacer.GetDelay(At(1), TimeSpan.FromMilliseconds(5)));
        AssertDelay(0, pacer.GetDelay(At(1), TimeSpan.Zero));
    }

    [TestMethod]
    public void ShortenedIntervalStartsANewGrid()
    {
        CapturePacer pacer = new();
        Capture(pacer, 0, 1, interval: TimeSpan.FromMilliseconds(100));

        // Captured at 3 ms, before it was due even with the new interval (5 ms; e.g. the wait ended early): the grid starts at this capture, the next one is due at 8 ms.
        Capture(pacer, 3, 4, interval: TimeSpan.FromMilliseconds(5));
        AssertDelay(4, pacer.GetDelay(At(4), TimeSpan.FromMilliseconds(5)));
    }

    [TestMethod]
    public void FailedCapturesBackOff()
    {
        CapturePacer pacer = new();

        List<double> delays = [];
        double now = 0;
        for (int i = 0; i < 7; i++)
        {
            Capture(pacer, now, now, CaptureResult.Failed, TimeSpan.Zero);
            double delay = pacer.GetDelay(At(now), TimeSpan.Zero).TotalMilliseconds;
            delays.Add(Math.Round(delay));
            now += delay;
        }

        CollectionAssert.AreEqual(new double[] { 100, 200, 400, 800, 1600, 2000, 2000 }, delays);
    }

    [TestMethod]
    public void BackoffCountsFromTheEndOfTheCapture()
    {
        CapturePacer pacer = new();

        Capture(pacer, 0, 50, CaptureResult.Failed);
        AssertDelay(100, pacer.GetDelay(At(50), INTERVAL));
    }

    [TestMethod]
    public void LongerIntervalWinsOverTheBackoff()
    {
        CapturePacer pacer = new();

        Capture(pacer, 0, 0, CaptureResult.Failed, TimeSpan.FromMilliseconds(500));
        AssertDelay(500, pacer.GetDelay(At(0), TimeSpan.FromMilliseconds(500)));
    }

    [TestMethod]
    [DataRow(CaptureResult.NewFrame)]
    [DataRow(CaptureResult.NoChange)]
    [DataRow(CaptureResult.Timeout)]
    [DataRow(CaptureResult.Restarting)]
    [DataRow(CaptureResult.Unavailable)]
    public void OtherResultsResetTheBackoff(CaptureResult result)
    {
        CapturePacer pacer = new();
        for (int i = 0; i < 4; i++)
            Capture(pacer, 0, 0, CaptureResult.Failed, TimeSpan.Zero);

        // Unavailable included: the capture waits for its restarts itself.
        Capture(pacer, 0, 0, result, TimeSpan.Zero);
        AssertDelay(0, pacer.GetDelay(At(0), TimeSpan.Zero));

        Capture(pacer, 0, 0, CaptureResult.Failed, TimeSpan.Zero);
        AssertDelay(100, pacer.GetDelay(At(0), TimeSpan.Zero));
    }

    private static void Capture(CapturePacer pacer, double started, double ended, CaptureResult result = CaptureResult.NewFrame, TimeSpan? interval = null)
        => pacer.Captured(At(started), At(ended), result, interval ?? INTERVAL);

    private static long At(double milliseconds) => START + (long)(milliseconds / 1000 * Stopwatch.Frequency);

    // Stopwatch ticks and TimeSpan ticks can differ, so conversions may be off by a tick.
    private static void AssertDelay(double expectedMilliseconds, TimeSpan delay)
        => Assert.AreEqual(expectedMilliseconds, delay.TotalMilliseconds, 0.001);
}
