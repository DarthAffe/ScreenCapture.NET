using System.Diagnostics;

namespace ScreenCapture.NET.Tests;

[TestClass]
public sealed class CaptureResultTests
{
    [TestMethod]
    [DataRow(CaptureResult.NewFrame)]
    [DataRow(CaptureResult.NoChange)]
    [DataRow(CaptureResult.Timeout)]
    [DataRow(CaptureResult.Restarting)]
    [DataRow(CaptureResult.Unavailable)]
    [DataRow(CaptureResult.Failed)]
    public void ResultIsReturnedAndReported(CaptureResult result)
    {
        using TestScreenCapture screenCapture = new() { Result = result };

        ScreenCaptureUpdatedEventArgs? args = null;
        screenCapture.Updated += (_, e) => args = e;

        Assert.AreEqual(result, screenCapture.CaptureScreen());
        Assert.IsNotNull(args);
        Assert.AreEqual(result, args.Value.Result);
    }

    [TestMethod]
    public void ExceptionIsReportedAsFailed()
    {
        using TestScreenCapture screenCapture = new() { CaptureException = new InvalidOperationException("test") };

        ScreenCaptureUpdatedEventArgs? args = null;
        screenCapture.Updated += (_, e) => args = e;

        Assert.AreEqual(CaptureResult.Failed, screenCapture.CaptureScreen());
        Assert.AreEqual(new ScreenCaptureUpdatedEventArgs(CaptureResult.Failed, 0, 0), args);
    }

    [TestMethod]
    public void NoFrameYetReportsZero()
    {
        using TestScreenCapture screenCapture = new() { Result = CaptureResult.Timeout, Timestamp = 123 };

        ScreenCaptureUpdatedEventArgs? args = null;
        screenCapture.Updated += (_, e) => args = e;

        screenCapture.CaptureScreen();

        Assert.AreEqual(new ScreenCaptureUpdatedEventArgs(CaptureResult.Timeout, 0, 0), args);
    }

    [TestMethod]
    public void FrameNumberAndTimestampDescribeTheLatestNewFrame()
    {
        using TestScreenCapture screenCapture = new();

        List<ScreenCaptureUpdatedEventArgs> args = [];
        screenCapture.Updated += (_, e) => args.Add(e);

        Capture(CaptureResult.NewFrame, 100);
        Capture(CaptureResult.NoChange, 200);
        Capture(CaptureResult.Timeout, 300);
        Capture(CaptureResult.NewFrame, 400);
        Capture(CaptureResult.Restarting, 500);
        Capture(CaptureResult.Unavailable, 600);
        Capture(CaptureResult.NewFrame, 700);
        screenCapture.CaptureException = new InvalidOperationException("test");
        screenCapture.CaptureScreen();

        CollectionAssert.AreEqual(new[]
                                  {
                                      new ScreenCaptureUpdatedEventArgs(CaptureResult.NewFrame, 1, 100),
                                      new ScreenCaptureUpdatedEventArgs(CaptureResult.NoChange, 1, 100),
                                      new ScreenCaptureUpdatedEventArgs(CaptureResult.Timeout, 1, 100),
                                      new ScreenCaptureUpdatedEventArgs(CaptureResult.NewFrame, 2, 400),
                                      new ScreenCaptureUpdatedEventArgs(CaptureResult.Restarting, 2, 400),
                                      new ScreenCaptureUpdatedEventArgs(CaptureResult.Unavailable, 2, 400),
                                      new ScreenCaptureUpdatedEventArgs(CaptureResult.NewFrame, 3, 700),
                                      new ScreenCaptureUpdatedEventArgs(CaptureResult.Failed, 3, 700)
                                  }, args);

        void Capture(CaptureResult result, long timestamp)
        {
            screenCapture.Result = result;
            screenCapture.Timestamp = timestamp;
            screenCapture.CaptureScreen();
        }
    }

    [TestMethod]
    public void MissingTimestampIsTheTimeOfTheCapture()
    {
        using TestScreenCapture screenCapture = new();

        ScreenCaptureUpdatedEventArgs? args = null;
        screenCapture.Updated += (_, e) => args = e;

        long before = Stopwatch.GetTimestamp();
        screenCapture.CaptureScreen();
        long after = Stopwatch.GetTimestamp();

        Assert.IsNotNull(args);
        Assert.IsInRange(before, after, args.Value.Timestamp);
    }

    [TestMethod]
    public void RaisingTheEventAllocatesNothing()
    {
        using TestScreenCapture screenCapture = new();

        long frames = 0;
        screenCapture.Updated += (_, e) => frames = e.FrameNumber;

        // Warm up (JIT, tiering) before measuring.
        for (int i = 0; i < 100; i++)
            screenCapture.CaptureScreen();

        long allocated = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++)
            screenCapture.CaptureScreen();
        allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;

        Assert.AreEqual(1100, frames);
        Assert.AreEqual(0, allocated, "Capturing allocated memory.");
    }
}
