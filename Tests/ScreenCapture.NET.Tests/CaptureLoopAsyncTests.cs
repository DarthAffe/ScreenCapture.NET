using System.Diagnostics;

namespace ScreenCapture.NET.Tests;

/// <summary>
/// The async capture loop, in real time (see <see cref="CapturePacerTests"/> for the exact pacing and backoff).
/// </summary>
[TestClass]
public sealed class CaptureLoopAsyncTests
{
    [TestMethod]
    public async Task YieldsTheResultsOfTheCapture()
    {
        using TestScreenCapture screenCapture = new();

        List<CaptureResult> results = [];
        await foreach (CaptureResult result in screenCapture.CaptureLoopAsync())
        {
            results.Add(result);
            if (results.Count == 3) break;

            screenCapture.Result = results.Count == 1 ? CaptureResult.NoChange : CaptureResult.Timeout;
        }

        CollectionAssert.AreEqual(new[] { CaptureResult.NewFrame, CaptureResult.NoChange, CaptureResult.Timeout }, results);
    }

    [TestMethod]
    public async Task CapturesOnlyWhenAsked()
    {
        using TestScreenCapture screenCapture = new();
        ScreenCaptureLoopTests.CaptureCounter counter = new(screenCapture);

        int results = 0;
        await foreach (CaptureResult _ in screenCapture.CaptureLoopAsync())
        {
            // Nothing is captured while the consumer handles a result.
            await Task.Delay(30);
            Assert.AreEqual(++results, counter.Count);

            if (results == 3) break;
        }

        await Task.Delay(30);
        Assert.AreEqual(3, counter.Count);
    }

    [TestMethod]
    public async Task LoopIsPaced()
    {
        using TestScreenCapture screenCapture = new();

        Stopwatch stopwatch = Stopwatch.StartNew();
        int results = 0;
        await foreach (CaptureResult _ in screenCapture.CaptureLoopAsync(50))
            if (++results == 6)
                break;

        // One capture right away, then one per 20 ms (late ones are followed sooner, but never before their time on the grid).
        Assert.IsGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(100), stopwatch.Elapsed);
    }

    [TestMethod]
    public async Task FailedCapturesBackOff()
    {
        using TestScreenCapture screenCapture = new() { Result = CaptureResult.Failed };

        Stopwatch stopwatch = Stopwatch.StartNew();
        int results = 0;
        await foreach (CaptureResult _ in screenCapture.CaptureLoopAsync())
            if (++results == 3)
                break;

        // Without a limit, but after failures: at 0, 100 and 300 ms.
        Assert.IsGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(300), stopwatch.Elapsed);
    }

    [TestMethod]
    public async Task DisposedCaptureEndsTheSequence()
    {
        TestScreenCapture screenCapture = new();

        int results = 0;
        await foreach (CaptureResult _ in screenCapture.CaptureLoopAsync())
            if (++results == 2)
                screenCapture.Dispose();

        Assert.AreEqual(2, results);
    }

    [TestMethod]
    public async Task CancellationThrows()
    {
        using TestScreenCapture screenCapture = new();
        using CancellationTokenSource cancellation = new();

        int results = 0;
        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
        {
            await foreach (CaptureResult _ in screenCapture.CaptureLoopAsync(cancellationToken: cancellation.Token))
                if (++results == 2)
                    await cancellation.CancelAsync();
        });

        Assert.AreEqual(2, results);
    }

    [TestMethod]
    public async Task CancellationEndsTheWait()
    {
        using TestScreenCapture screenCapture = new();
        using CancellationTokenSource cancellation = new();

        // The first capture is right away, then the sequence waits 2 s.
        Stopwatch stopwatch = Stopwatch.StartNew();
        cancellation.CancelAfter(50);
        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
        {
            // WithCancellation instead of the parameter: both cancel the sequence.
            await foreach (CaptureResult _ in screenCapture.CaptureLoopAsync(0.5).WithCancellation(cancellation.Token)) { }
        });

        Assert.IsLessThan(TimeSpan.FromSeconds(1), stopwatch.Elapsed);
    }

    [TestMethod]
    public async Task UnexpectedExceptionIsThrown()
    {
        InvalidOperationException exception = new("test");
        using ScreenCaptureLoopTests.ThrowingScreenCapture screenCapture = new(exception);

        InvalidOperationException thrown = await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
        {
            await foreach (CaptureResult _ in screenCapture.CaptureLoopAsync()) { }
        });

        Assert.AreSame(exception, thrown);
    }

    [TestMethod]
    public void InvalidArgumentsThrowWhenCalled()
    {
        using TestScreenCapture screenCapture = new();

        // Not enumerated: the arguments are checked by the call.
        Assert.ThrowsExactly<ArgumentNullException>(() => ScreenCaptureExtensions.CaptureLoopAsync(null!));

        ArgumentOutOfRangeException exception = Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => screenCapture.CaptureLoopAsync(-1));
        Assert.AreEqual("targetFrameRate", exception.ParamName);
    }
}
