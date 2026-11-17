using System.Diagnostics;
using HPPH;

namespace ScreenCapture.NET.Tests;

/// <summary>
/// The background loop, in real time (see <see cref="CapturePacerTests"/> for the exact pacing and backoff).
/// </summary>
[TestClass]
public sealed class ScreenCaptureLoopTests
{
    private static readonly TimeSpan TIMEOUT = TimeSpan.FromSeconds(10);

    // Waits that must end early are much shorter than the 2 s they cut.
    private static readonly TimeSpan CUT_SHORT = TimeSpan.FromSeconds(1);

    [TestMethod]
    public void LoopCapturesUntilStopped()
    {
        using TestScreenCapture screenCapture = new();
        CaptureCounter counter = new(screenCapture);

        using ScreenCaptureLoop loop = new(screenCapture);
        loop.Start();
        Assert.IsTrue(loop.IsRunning);

        counter.WaitFor(3);
        loop.Stop();

        Assert.IsFalse(loop.IsRunning);
        Assert.IsTrue(loop.Completion.IsCompletedSuccessfully);

        // Stop waited for the loop to end: nothing is captured anymore.
        int captures = counter.Count;
        Thread.Sleep(50);
        Assert.AreEqual(captures, counter.Count);
    }

    [TestMethod]
    public void LoopIsPaced()
    {
        using TestScreenCapture screenCapture = new();
        CaptureCounter counter = new(screenCapture);

        using ScreenCaptureLoop loop = new(screenCapture, 50);

        Stopwatch stopwatch = Stopwatch.StartNew();
        loop.Start();
        Thread.Sleep(300);
        loop.Stop();
        stopwatch.Stop();

        // One capture right away, then at most one per 20 ms (+1 for timing at the edges).
        int maximum = (int)(stopwatch.Elapsed.TotalMilliseconds / 20) + 2;
        Assert.IsGreaterThanOrEqualTo(2, counter.Count);
        Assert.IsLessThanOrEqualTo(maximum, counter.Count);
    }

    [TestMethod]
    public void FailedCapturesBackOff()
    {
        using TestScreenCapture screenCapture = new() { Result = CaptureResult.Failed };
        CaptureCounter counter = new(screenCapture);

        using ScreenCaptureLoop loop = new(screenCapture);
        loop.Start();
        Thread.Sleep(350);
        loop.Stop();

        // Without a limit, but after failures: at 0, 100 and 300 ms.
        Assert.IsGreaterThanOrEqualTo(2, counter.Count);
        Assert.IsLessThanOrEqualTo(4, counter.Count);
    }

    [TestMethod]
    public void ChangedFrameRateEndsTheWait()
    {
        using TestScreenCapture screenCapture = new();
        CaptureCounter counter = new(screenCapture);

        using ScreenCaptureLoop loop = new(screenCapture, 0.5);
        loop.Start();
        counter.WaitFor(1);

        // The loop waits 2 s for the next capture, but the new rate applies right away.
        Stopwatch stopwatch = Stopwatch.StartNew();
        loop.TargetFrameRate = null;
        counter.WaitFor(2);

        Assert.IsLessThan(CUT_SHORT, stopwatch.Elapsed);
        Assert.IsNull(loop.TargetFrameRate);
    }

    [TestMethod]
    public void StopEndsTheWait()
    {
        using TestScreenCapture screenCapture = new();
        CaptureCounter counter = new(screenCapture);

        using ScreenCaptureLoop loop = new(screenCapture, 0.5);
        loop.Start();
        counter.WaitFor(1);

        Stopwatch stopwatch = Stopwatch.StartNew();
        loop.Stop();

        Assert.IsLessThan(CUT_SHORT, stopwatch.Elapsed);
        Assert.AreEqual(1, counter.Count);
    }

    [TestMethod]
    public void StopInAHandlerDoesntWait()
    {
        using TestScreenCapture screenCapture = new();

        using ScreenCaptureLoop loop = new(screenCapture);
        int captures = 0;
        screenCapture.Updated += (_, _) =>
        {
            captures++;
            loop.Stop();
        };

        loop.Start();

        Assert.IsTrue(loop.Completion.Wait(TIMEOUT));
        Assert.IsFalse(loop.IsRunning);
        Assert.AreEqual(1, captures);
    }

    [TestMethod]
    public void RestartInAHandlerDoesntOverlapCaptures()
    {
        using TestScreenCapture screenCapture = new();

        using ScreenCaptureLoop loop = new(screenCapture);
        int active = 0;
        int captures = 0;
        bool overlapped = false;
        using ManualResetEventSlim captured = new();

        screenCapture.Updated += (_, _) =>
        {
            if (Interlocked.Increment(ref active) > 1)
                overlapped = true;

            // The first capture restarts the loop and is still running for a while: the new run must wait for it.
            if (Interlocked.Increment(ref captures) == 1)
            {
                loop.Stop();
                loop.Start();
                Thread.Sleep(50);
            }
            else if (captures >= 3)
                captured.Set();

            Interlocked.Decrement(ref active);
        };

        loop.Start();
        Assert.IsTrue(captured.Wait(TIMEOUT));
        loop.Stop();

        Assert.IsFalse(overlapped);
    }

    [TestMethod]
    public void StopAsyncDoesntWaitForAHeldFrame()
    {
        using TestScreenCapture screenCapture = new();
        CaptureZone<ColorARGB> zone = screenCapture.RegisterCaptureZone(new CaptureRegion(0, 0, 4, 4), doubleBuffered: false);
        CaptureCounter counter = new(screenCapture);

        using ScreenCaptureLoop loop = new(screenCapture);
        loop.Start();
        counter.WaitFor(1);

        Task stopped;
        using (zone.Lock())
        {
            // The loop now waits for the frame to write the single-buffered zone; Stop would wait forever.
            Thread.Sleep(50);
            stopped = loop.StopAsync();

            Assert.IsFalse(loop.IsRunning);
            Assert.IsFalse(stopped.Wait(50));
        }

        Assert.IsTrue(stopped.Wait(TIMEOUT));
        Assert.AreSame(loop.Completion, stopped);
    }

    [TestMethod]
    public void DisposedCaptureEndsTheLoop()
    {
        using TestScreenCapture screenCapture = new();
        CaptureCounter counter = new(screenCapture);

        using ScreenCaptureLoop loop = new(screenCapture, 100);
        loop.Start();
        counter.WaitFor(1);

        screenCapture.Dispose();

        Assert.IsTrue(loop.Completion.Wait(TIMEOUT));
        Assert.IsTrue(loop.Completion.IsCompletedSuccessfully);
        Assert.IsFalse(loop.IsRunning);
    }

    [TestMethod]
    public void UnexpectedExceptionEndsTheLoop()
    {
        InvalidOperationException exception = new("test");
        using ThrowingScreenCapture screenCapture = new(exception);

        using ScreenCaptureLoop loop = new(screenCapture);
        loop.Start();

        AggregateException thrown = Assert.ThrowsExactly<AggregateException>(() => loop.Completion.Wait(TIMEOUT));
        Assert.AreSame(exception, thrown.InnerException);
        Assert.IsFalse(loop.IsRunning);
    }

    [TestMethod]
    public void StartRunsOnce()
    {
        using TestScreenCapture screenCapture = new();

        using ScreenCaptureLoop loop = new(screenCapture, 100);
        loop.Start();
        Task completion = loop.Completion;
        loop.Start();

        Assert.AreSame(completion, loop.Completion);
    }

    [TestMethod]
    public void StoppedLoopCanBeStartedAgain()
    {
        using TestScreenCapture screenCapture = new();
        CaptureCounter counter = new(screenCapture);

        using ScreenCaptureLoop loop = new(screenCapture);
        loop.Start();
        counter.WaitFor(1);
        Task first = loop.Completion;
        loop.Stop();

        int captures = counter.Count;
        loop.Start();
        counter.WaitFor(captures + 1);

        Assert.IsTrue(loop.IsRunning);
        Assert.IsTrue(first.IsCompletedSuccessfully);
        Assert.AreNotSame(first, loop.Completion);
    }

    [TestMethod]
    public void NeverStartedLoopIsCompleted()
    {
        using TestScreenCapture screenCapture = new();

        using ScreenCaptureLoop loop = new(screenCapture);
        loop.Stop();

        Assert.IsFalse(loop.IsRunning);
        Assert.IsTrue(loop.Completion.IsCompletedSuccessfully);
        Assert.IsTrue(loop.StopAsync().IsCompletedSuccessfully);
    }

    [TestMethod]
    public void DisposeStopsTheLoopButNotTheCapture()
    {
        using TestScreenCapture screenCapture = new();

        ScreenCaptureLoop loop = new(screenCapture);
        loop.Start();
        loop.Dispose();
        loop.Dispose();

        Assert.IsFalse(loop.IsRunning);
        Assert.IsTrue(loop.Completion.IsCompletedSuccessfully);
        Assert.IsFalse(screenCapture.IsDisposed);
        Assert.ThrowsExactly<ObjectDisposedException>(loop.Start);
    }

    [TestMethod]
    public void InvalidArgumentsThrow()
    {
        using TestScreenCapture screenCapture = new();

        Assert.ThrowsExactly<ArgumentNullException>(() => new ScreenCaptureLoop(null!));

        ArgumentOutOfRangeException exception = Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new ScreenCaptureLoop(screenCapture, 0));
        Assert.AreEqual("targetFrameRate", exception.ParamName);

        using ScreenCaptureLoop loop = new(screenCapture, 30);
        Assert.AreEqual(30, loop.TargetFrameRate);

        exception = Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => loop.TargetFrameRate = double.NaN);
        Assert.AreEqual("value", exception.ParamName);
        Assert.AreEqual(30, loop.TargetFrameRate);
    }

    /// <summary>
    /// Counts the captures of a screen capture.
    /// </summary>
    internal sealed class CaptureCounter
    {
        private int _count;

        public int Count => Volatile.Read(ref _count);

        public CaptureCounter(IScreenCapture screenCapture)
        {
            screenCapture.Updated += (_, _) => Interlocked.Increment(ref _count);
        }

        public void WaitFor(int count)
            => Assert.IsTrue(SpinWait.SpinUntil(() => Count >= count, TIMEOUT), $"Waited for {count} captures, got {Count}.");
    }

    /// <summary>
    /// A capture that breaks the contract of <see cref="IScreenCapture.CaptureScreen"/> by throwing.
    /// </summary>
    internal sealed class ThrowingScreenCapture(Exception exception) : TestScreenCapture
    {
        public override CaptureResult CaptureScreen() => throw exception;
    }
}
