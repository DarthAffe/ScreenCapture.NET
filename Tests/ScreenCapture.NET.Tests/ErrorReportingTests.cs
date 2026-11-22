using System.Globalization;
using HPPH;

namespace ScreenCapture.NET.Tests;

[TestClass]
public sealed class ErrorReportingTests
{
    [TestMethod]
    public void NoErrorLeavesLastErrorEmpty()
    {
        using TestScreenCapture screenCapture = new();
        screenCapture.RegisterCaptureZone(new CaptureRegion(0, 0, 10, 10));

        List<Exception> errors = [];
        screenCapture.ErrorOccurred += (_, e) => errors.Add(e.Exception);

        Assert.IsNull(screenCapture.LastError);
        Assert.AreEqual(CaptureResult.NewFrame, screenCapture.CaptureScreen());
        Assert.IsNull(screenCapture.LastError);
        Assert.IsEmpty(errors);
    }

    [TestMethod]
    public void CaptureExceptionIsReportedBeforeUpdated()
    {
        InvalidOperationException exception = new("test");
        using TestScreenCapture screenCapture = new() { CaptureException = exception };

        List<string> events = [];
        screenCapture.ErrorOccurred += (_, e) =>
        {
            Assert.AreSame(exception, e.Exception);
            Assert.AreSame(exception, screenCapture.LastError);
            events.Add("error");
        };
        screenCapture.Updated += (_, _) => events.Add("updated");

        Assert.AreEqual(CaptureResult.Failed, screenCapture.CaptureScreen());

        Assert.AreSame(exception, screenCapture.LastError);
        CollectionAssert.AreEqual(new[] { "error", "updated" }, events);
    }

    [TestMethod]
    public void ZoneUpdateExceptionIsReported()
    {
        InvalidOperationException exception = new("test");
        using TestScreenCapture screenCapture = new() { ZoneUpdating = _ => throw exception };
        CaptureZone<ColorARGB> zone = screenCapture.RegisterCaptureZone(new CaptureRegion(0, 0, 10, 10));

        bool zoneUpdated = false;
        zone.Updated += (_, _) => zoneUpdated = true;

        List<Exception> errors = [];
        screenCapture.ErrorOccurred += (_, e) => errors.Add(e.Exception);

        // The capture itself worked.
        Assert.AreEqual(CaptureResult.NewFrame, screenCapture.CaptureScreen());

        Assert.IsFalse(zoneUpdated);
        Assert.AreSame(exception, screenCapture.LastError);
        CollectionAssert.AreEqual(new[] { exception }, errors);
    }

    /// <summary>
    /// Backends report errors while holding their capture lock. A handler that locks a zone there could deadlock
    /// against a thread holding a frame of that zone and calling into the capture, so the event must be raised later.
    /// </summary>
    [TestMethod]
    public void RecoveredErrorIsRaisedOutsideTheCaptureLock()
    {
        InvalidOperationException exception = new("test");
        using TestScreenCapture screenCapture = new() { Result = CaptureResult.Restarting, ReportedCaptureError = exception };

        bool? lockHeld = null;
        screenCapture.ErrorOccurred += (_, e) =>
        {
            Assert.AreSame(exception, e.Exception);
            lockHeld = screenCapture.CaptureLock.IsHeldByCurrentThread;
        };

        Assert.AreEqual(CaptureResult.Restarting, screenCapture.CaptureScreen());

        Assert.IsNotNull(lockHeld, "ErrorOccurred wasn't raised.");
        Assert.IsFalse(lockHeld.Value, "ErrorOccurred was raised while the capture lock was held.");
        Assert.AreSame(exception, screenCapture.LastError);
    }

    [TestMethod]
    public void ErrorsOutsideOfACaptureAreRaisedByTheNextCapture()
    {
        InvalidOperationException first = new("first");
        InvalidOperationException second = new("second");
        using TestScreenCapture screenCapture = new();

        List<Exception> errors = [];
        screenCapture.ErrorOccurred += (_, e) => errors.Add(e.Exception);

        // E.g. a failed restart in the constructor or in Restart().
        screenCapture.Report(first);
        screenCapture.Report(second);

        Assert.AreSame(second, screenCapture.LastError);
        Assert.IsEmpty(errors);

        screenCapture.CaptureScreen();
        CollectionAssert.AreEqual(new[] { first, second }, errors);

        // Raised once.
        screenCapture.CaptureScreen();
        Assert.HasCount(2, errors);
    }

    [TestMethod]
    public void LastErrorIsKeptAfterRecovery()
    {
        InvalidOperationException exception = new("test");
        using TestScreenCapture screenCapture = new() { CaptureException = exception };

        int errorCount = 0;
        screenCapture.ErrorOccurred += (_, _) => errorCount++;

        Assert.AreEqual(CaptureResult.Failed, screenCapture.CaptureScreen());

        screenCapture.CaptureException = null;
        for (int i = 0; i < 3; i++)
            Assert.AreEqual(CaptureResult.NewFrame, screenCapture.CaptureScreen());

        Assert.AreSame(exception, screenCapture.LastError);
        Assert.AreEqual(1, errorCount);
    }

    [TestMethod]
    public void OnlyTheLatestPendingErrorsAreKept()
    {
        using TestScreenCapture screenCapture = new();

        List<Exception> errors = [];
        screenCapture.ErrorOccurred += (_, e) => errors.Add(e.Exception);

        List<Exception> reported = [.. Enumerable.Range(0, 100).Select(i => new InvalidOperationException(i.ToString(CultureInfo.InvariantCulture)))];
        foreach (Exception exception in reported)
            screenCapture.Report(exception);

        screenCapture.CaptureScreen();

        Assert.AreSame(reported[^1], screenCapture.LastError);
        Assert.HasCount(16, errors);
        CollectionAssert.AreEqual(reported[^16..], errors);
    }

    [TestMethod]
    public void ExceptionOfAnErrorHandlerIsIgnored()
    {
        using TestScreenCapture screenCapture = new() { CaptureException = new InvalidOperationException("test") };

        bool updated = false;
        screenCapture.ErrorOccurred += (_, _) => throw new InvalidOperationException("handler");
        screenCapture.Updated += (_, _) => updated = true;

        Assert.AreEqual(CaptureResult.Failed, screenCapture.CaptureScreen());
        Assert.IsTrue(updated);
        Assert.AreEqual("test", screenCapture.LastError?.Message);
    }

    [TestMethod]
    public void ExceptionsOfUpdatedHandlersAreNotReported()
    {
        using TestScreenCapture screenCapture = new();
        CaptureZone<ColorARGB> zone = screenCapture.RegisterCaptureZone(new CaptureRegion(0, 0, 10, 10));

        zone.Updated += (_, _) => throw new InvalidOperationException("zone handler");
        screenCapture.Updated += (_, _) => throw new InvalidOperationException("capture handler");

        List<Exception> errors = [];
        screenCapture.ErrorOccurred += (_, e) => errors.Add(e.Exception);

        screenCapture.CaptureScreen();
        screenCapture.CaptureScreen();

        Assert.IsNull(screenCapture.LastError);
        Assert.IsEmpty(errors);
    }
}
