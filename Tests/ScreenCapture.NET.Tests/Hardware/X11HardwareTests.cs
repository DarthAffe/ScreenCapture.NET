using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using HPPH;

namespace ScreenCapture.NET.Tests.Hardware;

[TestClass]
[TestCategory(HardwareTestHelper.CATEGORY)]
[OSCondition(OperatingSystems.Linux)]
[SupportedOSPlatform("linux")]
public sealed class X11HardwareTests
{
    [TestInitialize]
    public void RequireXServer()
    {
        // Without an X server the service throws (see X11NoServerTests).
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")))
            Assert.Inconclusive("No X server available ($DISPLAY is not set).");
    }

    [TestMethod]
    public void EnumeratesDisplays()
    {
        using X11ScreenCaptureService service = new();
        HardwareTestHelper.AssertEnumeratesDisplays(service);
    }

    [TestMethod]
    public void CapturesFullScreenZone()
    {
        using X11ScreenCaptureService service = new();
        HardwareTestHelper.AssertCapturesZone(service, HardwareTestHelper.FullScreen);
    }

    [TestMethod]
    public void CapturesPartialZone()
    {
        using X11ScreenCaptureService service = new();
        HardwareTestHelper.AssertCapturesZone(service, HardwareTestHelper.Center);
    }

    [TestMethod]
    public void CapturesDownscaledZone()
    {
        using X11ScreenCaptureService service = new();
        HardwareTestHelper.AssertCapturesZone(service, HardwareTestHelper.Center, downscaleLevel: 2);
    }

    [TestMethod]
    public void MovedZoneCapturesNewPosition()
    {
        using X11ScreenCaptureService service = new();
        HardwareTestHelper.AssertMovedZoneCapturesNewPosition(service);
    }

    [TestMethod]
    public void MovedDownscaledZoneCapturesNewPosition()
    {
        using X11ScreenCaptureService service = new();
        HardwareTestHelper.AssertMovedZoneCapturesNewPosition(service, downscaleLevel: 2);
    }

    [TestMethod]
    public void ZonesCanBeChangedWhileCapturing()
    {
        using X11ScreenCaptureService service = new();
        HardwareTestHelper.AssertZonesCanBeChangedWhileCapturing(service);
    }

    [TestMethod]
    public void DownscaledZonesCanBeChangedWhileCapturing()
    {
        using X11ScreenCaptureService service = new();
        HardwareTestHelper.AssertZonesCanBeChangedWhileCapturing(service, downscaleLevel: 2);
    }

    [TestMethod]
    public void PartialZonesMatchFullScreen()
    {
        using X11ScreenCaptureService service = new();
        HardwareTestHelper.AssertPartialZonesMatchFullScreen(service);
    }

    [TestMethod]
    public void DisposeIsRepeatable()
    {
        using X11ScreenCaptureService service = new();
        HardwareTestHelper.AssertDisposeIsRepeatable(service);
    }

    [TestMethod]
    public void FailedRestartLeavesNoHandles()
    {
        // The screen doesn't exist, so every restart fails after the connection was opened.
        Display display = new(99, "missing", 640, 480, Rotation.None, new GraphicsCard(0, "first", 0, 0));
        X11ScreenCapture screenCapture = new(display);
        AssertNoHandles(screenCapture);

        CaptureZone<ColorBGRA> zone = screenCapture.RegisterCaptureZone(0, 0, 64, 64);

        for (int i = 0; i < 3; i++)
            Assert.IsFalse(screenCapture.CaptureScreen());
        AssertNoHandles(screenCapture);

        Assert.IsTrue(screenCapture.UnregisterCaptureZone(zone));

        screenCapture.Dispose();
        screenCapture.Dispose();
    }

    /// <summary>
    /// Restarts free the image while zones are copied from it on the capture thread.
    /// The zone update holds a reference on the image handle, so this must not read freed memory (which would crash the test host).
    /// </summary>
    [TestMethod]
    public void RestartWhileCapturingIsSafe()
    {
        const int restarts = 100;
        TimeSpan timeout = TimeSpan.FromSeconds(30);

        using X11ScreenCaptureService service = new();
        (_, Display display) = HardwareTestHelper.GetFirstDisplay(service);
        IScreenCapture screenCapture = service.GetScreenCapture(display);
        screenCapture.RegisterCaptureZone(0, 0, display.Width, display.Height);
        screenCapture.RegisterCaptureZone(0, 0, display.Width, display.Height, downscaleLevel: 2);
        HardwareTestHelper.CaptureFrame(screenCapture);

        using CancellationTokenSource cancellation = new();
        int captures = 0;
        Task capture = Task.Factory.StartNew(() =>
        {
            while (!cancellation.IsCancellationRequested)
            {
                screenCapture.CaptureScreen();
                captures++;
            }
        }, TaskCreationOptions.LongRunning);

        Task worker = Task.Factory.StartNew(() =>
        {
            for (int i = 0; i < restarts; i++)
                screenCapture.Restart();
        }, TaskCreationOptions.LongRunning);

        bool finished;
        try
        {
            finished = worker.Wait(timeout);
        }
        finally
        {
            cancellation.Cancel();
        }

        Assert.IsTrue(finished, "The restarts didn't finish (deadlock?).");
        Assert.IsTrue(capture.Wait(timeout), "The capture thread didn't finish (deadlock?).");
        Assert.IsGreaterThan(0, captures, "No capture ran during the restarts.");

        // Still works afterwards.
        HardwareTestHelper.CaptureFrame(screenCapture);
    }

    private static void AssertNoHandles(X11ScreenCapture screenCapture)
    {
        List<FieldInfo> fields = [.. typeof(X11ScreenCapture).GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
                                                             .Where(f => typeof(SafeHandle).IsAssignableFrom(f.FieldType))];
        Assert.HasCount(2, fields, "Handle fields not found.");

        foreach (FieldInfo field in fields)
            Assert.IsNull(field.GetValue(screenCapture), $"{field.Name} isn't empty.");
    }
}

/// <summary>
/// Runs only where libX11 is installed but no X server is configured (e.g. a headless Linux machine or a Wayland session without XWayland).
/// </summary>
[TestClass]
[TestCategory(HardwareTestHelper.CATEGORY)]
[OSCondition(OperatingSystems.Linux)]
[SupportedOSPlatform("linux")]
public sealed class X11NoServerTests
{
    [TestMethod]
    public void ServiceThrowsWithoutXServer()
    {
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")))
            Assert.Inconclusive("An X server is configured ($DISPLAY is set).");

        if (!NativeLibrary.TryLoad("libX11.so.6", out nint library))
            Assert.Inconclusive("libX11 isn't installed.");
        NativeLibrary.Free(library);

        using X11ScreenCaptureService service = new();

        InvalidOperationException exception = Assert.ThrowsExactly<InvalidOperationException>(() => service.GetGraphicsCards());
        StringAssert.Contains(exception.Message, "$DISPLAY");

        Assert.ThrowsExactly<InvalidOperationException>(() => service.GetDisplays(new GraphicsCard(0, "first", 0, 0)));
    }

    [TestMethod]
    public void CaptureFailsWithoutXServer()
    {
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")))
            Assert.Inconclusive("An X server is configured ($DISPLAY is set).");

        if (!NativeLibrary.TryLoad("libX11.so.6", out nint library))
            Assert.Inconclusive("libX11 isn't installed.");
        NativeLibrary.Free(library);

        // The capture doesn't throw: it fails and tries to reconnect on every call.
        using X11ScreenCapture screenCapture = new(new Display(0, "missing", 640, 480, Rotation.None, new GraphicsCard(0, "first", 0, 0)));
        screenCapture.RegisterCaptureZone(0, 0, 64, 64);

        Assert.IsFalse(screenCapture.CaptureScreen());
        Assert.IsFalse(screenCapture.CaptureScreen());
    }
}
