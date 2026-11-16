using System.Diagnostics;

namespace ScreenCapture.NET.Tests;

/// <summary>
/// How captures restart with the backoff (see <see cref="RestartBackoffTests"/> for the delays). These wait in real time, so they use the short delays.
/// </summary>
[TestClass]
public sealed class CaptureRestartTests
{
    // Waits that must not happen are checked against the shortest delay; ones cut short against half the delay they cut.
    private static readonly TimeSpan NO_WAIT = TimeSpan.FromMilliseconds(100);

    [TestMethod]
    public void FailedRestartsBackOff()
    {
        using TestScreenCapture screenCapture = new() { IsRunning = false, RestartSucceeds = false };

        for (int i = 0; i < 3; i++)
            Assert.AreEqual(CaptureResult.Unavailable, screenCapture.CaptureScreen());

        // Every call attempts a restart, the first one right away.
        Assert.HasCount(3, screenCapture.RestartAttempts);
        AssertWaited(100, screenCapture.RestartAttempts[0], screenCapture.RestartAttempts[1]);
        AssertWaited(200, screenCapture.RestartAttempts[1], screenCapture.RestartAttempts[2]);
    }

    [TestMethod]
    public void LostCaptureRestartsRightAway()
    {
        using TestScreenCapture screenCapture = new();

        Stopwatch stopwatch = Stopwatch.StartNew();

        foreach (CaptureResult result in Enum.GetValues<CaptureResult>())
        {
            screenCapture.Result = result;
            Assert.AreEqual(result, screenCapture.CaptureScreen());
        }

        screenCapture.IsRunning = false;
        Assert.AreEqual(CaptureResult.Restarting, screenCapture.CaptureScreen());

        screenCapture.Result = CaptureResult.NewFrame;
        Assert.AreEqual(CaptureResult.NewFrame, screenCapture.CaptureScreen());

        Assert.IsLessThan(NO_WAIT, stopwatch.Elapsed);
        Assert.HasCount(1, screenCapture.RestartAttempts);
    }

    [TestMethod]
    public void SuccessfulRestartResetsTheBackoff()
    {
        using TestScreenCapture screenCapture = new() { IsRunning = false, RestartSucceeds = false };

        // Restart calls aren't delayed, but count: the next attempt waits 200 ms.
        screenCapture.Restart();
        screenCapture.Restart();

        screenCapture.RestartSucceeds = true;
        Assert.AreEqual(CaptureResult.Restarting, screenCapture.CaptureScreen());
        Assert.AreEqual(CaptureResult.NewFrame, screenCapture.CaptureScreen());

        // Lost again: the first attempt is right away, the next one waits the shortest delay again (not 400 ms).
        screenCapture.IsRunning = false;
        screenCapture.RestartSucceeds = false;
        screenCapture.CaptureScreen();
        screenCapture.CaptureScreen();

        List<TimeSpan> attempts = screenCapture.RestartAttempts;
        Assert.HasCount(5, attempts);
        Assert.IsLessThan(NO_WAIT, attempts[1] - attempts[0]);
        AssertWaited(200, attempts[1], attempts[2]);
        Assert.IsLessThan(NO_WAIT, attempts[3] - attempts[2]);
        AssertWaited(100, attempts[3], attempts[4]);
        Assert.IsLessThan(TimeSpan.FromMilliseconds(200), attempts[4] - attempts[3]);
    }

    [TestMethod]
    public void SuccessfulRestartCallEndsTheWait()
    {
        using TestScreenCapture screenCapture = new() { IsRunning = false, RestartSucceeds = false };
        FailRestarts(screenCapture, 5); // The next attempt waits 1.6 s.

        Task restart = Task.Run(() =>
        {
            Thread.Sleep(100);
            screenCapture.RestartSucceeds = true;
            screenCapture.Restart();
        });

        Stopwatch stopwatch = Stopwatch.StartNew();
        Assert.AreEqual(CaptureResult.NewFrame, screenCapture.CaptureScreen());
        stopwatch.Stop();

        restart.Wait();
        Assert.IsLessThan(TimeSpan.FromMilliseconds(800), stopwatch.Elapsed);
        Assert.HasCount(6, screenCapture.RestartAttempts);
    }

    [TestMethod]
    public void DisposeEndsTheWait()
    {
        TestScreenCapture screenCapture = new() { IsRunning = false, RestartSucceeds = false };
        FailRestarts(screenCapture, 5); // The next attempt waits 1.6 s.

        Task dispose = Task.Run(() =>
        {
            Thread.Sleep(100);
            screenCapture.Dispose();
        });

        Stopwatch stopwatch = Stopwatch.StartNew();
        Assert.AreEqual(CaptureResult.Unavailable, screenCapture.CaptureScreen());
        stopwatch.Stop();

        dispose.Wait();
        Assert.IsLessThan(TimeSpan.FromMilliseconds(800), stopwatch.Elapsed);

        // No restart after the dispose.
        Assert.HasCount(5, screenCapture.RestartAttempts);
    }

    [TestMethod]
    public void RestartExceptionIsReportedAndBacksOff()
    {
        InvalidOperationException exception = new("test");
        using TestScreenCapture screenCapture = new() { IsRunning = false, RestartException = exception };

        List<Exception> errors = [];
        screenCapture.ErrorOccurred += (_, e) => errors.Add(e.Exception);

        Assert.AreEqual(CaptureResult.Unavailable, screenCapture.CaptureScreen());
        Assert.AreEqual(CaptureResult.Unavailable, screenCapture.CaptureScreen());

        Assert.AreSame(exception, screenCapture.LastError);
        CollectionAssert.AreEqual(new[] { exception, exception }, errors);
        Assert.HasCount(2, screenCapture.RestartAttempts);
        AssertWaited(100, screenCapture.RestartAttempts[0], screenCapture.RestartAttempts[1]);
    }

    private static void FailRestarts(TestScreenCapture screenCapture, int count)
    {
        for (int i = 0; i < count; i++)
            screenCapture.Restart();
    }

    // The next attempt is due the delay after the previous one failed, which is after it started.
    private static void AssertWaited(int milliseconds, TimeSpan previousAttempt, TimeSpan attempt)
        => Assert.IsGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(milliseconds), attempt - previousAttempt);
}
