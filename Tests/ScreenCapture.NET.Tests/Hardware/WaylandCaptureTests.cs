extern alias Wayland;
using System.Diagnostics;
using System.Runtime.Versioning;
using HPPH;
using Wayland::ScreenCapture.NET;
using static Wayland::ScreenCapture.NET.PipeWire;

namespace ScreenCapture.NET.Tests.Hardware;

/// <summary>
/// Captures a <see cref="PipeWireVideoSource"/> on the PipeWire daemon of the user, shared by a <see cref="FakeScreenCastPortal"/>: no dialog, no compositor needed.
/// Needs PipeWire with a session manager (WirePlumber); inconclusive in a process that also runs the Interactive tests (see <see cref="PrivateSessionBus"/>).
/// </summary>
[TestClass]
[TestCategory(HardwareTestHelper.CATEGORY)]
[OSCondition(OperatingSystems.Linux)]
[SupportedOSPlatform("linux")]
public sealed class WaylandCaptureTests
{
    private const int WIDTH = 64;
    private const int HEIGHT = 48;

    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(5);
    private static readonly ColorBGRA Color = new(10, 20, 30, 0);

    [TestMethod]
    public void ZonesShowTheFramesOpaque()
    {
        using Setup setup = Setup.Start();
        CaptureZone<ColorBGRA> zone = setup.Capture.RegisterCaptureZone(new CaptureRegion(0, 0, WIDTH, HEIGHT));

        setup.CaptureFrame(Color);

        // BGRx: the padding byte (0 here) becomes alpha 255.
        AssertPixels(zone, new ColorBGRA(10, 20, 30, 255));
    }

    [TestMethod]
    public void BGRAKeepsItsAlpha()
    {
        using Setup setup = Setup.Start(videoFormat: SPA_VIDEO_FORMAT_BGRA);
        CaptureZone<ColorBGRA> zone = setup.Capture.RegisterCaptureZone(new CaptureRegion(0, 0, WIDTH, HEIGHT));

        setup.CaptureFrame(new ColorBGRA(10, 20, 30, 128));

        AssertPixels(zone, new ColorBGRA(10, 20, 30, 128));
    }

    /// <summary>
    /// The source fills the padding at the end of its rows with another color.
    /// </summary>
    [TestMethod]
    public void PaddedRowsAreRead()
    {
        using Setup setup = Setup.Start(stride: (WIDTH * 4) + 32);
        CaptureZone<ColorBGRA> zone = setup.Capture.RegisterCaptureZone(new CaptureRegion(0, 0, WIDTH, HEIGHT));

        setup.CaptureFrame(Color);

        AssertPixels(zone, new ColorBGRA(10, 20, 30, 255));
    }

    [TestMethod]
    public void DownscaledZonesShowTheFrames()
    {
        using Setup setup = Setup.Start();
        CaptureZone<ColorBGRA> zone = setup.Capture.RegisterCaptureZone(new CaptureRegion(8, 8, 32, 16), downscaleLevel: 2);

        setup.CaptureFrame(Color);

        Assert.AreEqual((8, 4), (zone.Width, zone.Height));
        AssertPixels(zone, new ColorBGRA(10, 20, 30, 255));
    }

    [TestMethod]
    public void NoFrameIsATimeoutAndKeepsTheZones()
    {
        using Setup setup = Setup.Start();
        CaptureZone<ColorBGRA> zone = setup.Capture.RegisterCaptureZone(new CaptureRegion(0, 0, WIDTH, HEIGHT));
        setup.CaptureFrame(Color);

        Assert.AreEqual(CaptureResult.Timeout, setup.Capture.CaptureScreen());
        AssertPixels(zone, new ColorBGRA(10, 20, 30, 255));
    }

    /// <summary>
    /// Between two captures the capture holds two buffers (the current frame and the newest one), so with fewer than three the source can't send a newer frame:
    /// a capture that runs seldom would get the frame from right after the previous capture.
    /// The buffers are offered like the compositors do: xdg-desktop-portal-wlr 2 (2 to 32), KWin 3 (2 to 4), mutter 8 (2 to 8).
    /// </summary>
    [TestMethod]
    [DataRow(2, 2, 32)]
    [DataRow(3, 2, 4)]
    [DataRow(8, 2, 8)]
    [DataRow(8, 8, 8)]
    public void OnlyTheNewestFrameIsCaptured(int defaultBuffers, int minBuffers, int maxBuffers)
    {
        using Setup setup = Setup.Start(buffers: (defaultBuffers, minBuffers, maxBuffers));
        CaptureZone<ColorBGRA> zone = setup.Capture.RegisterCaptureZone(new CaptureRegion(0, 0, WIDTH, HEIGHT));
        setup.CaptureFrame(Color);

        // Apart, like a compositor sends them (at most one per refresh): PipeWire drops a trigger while the previous cycle runs.
        setup.Source.Send(new ColorBGRA(0, 0, 255, 0));
        Thread.Sleep(100);
        setup.Source.Send(new ColorBGRA(0, 255, 0, 0));
        Thread.Sleep(100);

        Assert.AreEqual(CaptureResult.NewFrame, setup.Capture.CaptureScreen());
        AssertPixels(zone, new ColorBGRA(0, 255, 0, 255));

        // The older one was dropped, not queued.
        Assert.AreEqual(CaptureResult.Timeout, setup.Capture.CaptureScreen());
    }

    /// <summary>
    /// More frames than the source has buffers: the capture has to give every buffer back.
    /// A source that offers at most two buffers (fewer than the capture prefers) still streams.
    /// </summary>
    [TestMethod]
    [DataRow(8, 8, 8)]
    [DataRow(2, 2, 2)]
    public void BuffersAreGivenBack(int defaultBuffers, int minBuffers, int maxBuffers)
    {
        using Setup setup = Setup.Start(buffers: (defaultBuffers, minBuffers, maxBuffers));
        CaptureZone<ColorBGRA> zone = setup.Capture.RegisterCaptureZone(new CaptureRegion(0, 0, WIDTH, HEIGHT));

        for (byte i = 0; i < 30; i++)
        {
            setup.CaptureFrame(new ColorBGRA(i, 0, 0, 0));
            AssertPixels(zone, new ColorBGRA(i, 0, 0, 255));
        }
    }

    [TestMethod]
    public void CorruptedFramesAreSkipped()
    {
        using Setup setup = Setup.Start();
        CaptureZone<ColorBGRA> zone = setup.Capture.RegisterCaptureZone(new CaptureRegion(0, 0, WIDTH, HEIGHT));
        setup.CaptureFrame(Color);

        setup.Source.Send(new ColorBGRA(0, 0, 255, 0), corrupted: true);

        Assert.AreEqual(CaptureResult.Timeout, setup.Capture.CaptureScreen());
        AssertPixels(zone, new ColorBGRA(10, 20, 30, 255));
    }

    /// <summary>
    /// Like a monitor that gets another resolution: the stream negotiates a new format, the buffers of the old size are removed.
    /// </summary>
    [TestMethod]
    public void ResizedStreamChangesTheDisplay()
    {
        using Setup setup = Setup.Start();
        CaptureZone<ColorBGRA> zone = setup.Capture.RegisterCaptureZone(new CaptureRegion(0, 0, WIDTH, HEIGHT));
        setup.CaptureFrame(Color);

        List<Display> changes = [];
        setup.Capture.DisplayChanged += (_, args) => changes.Add(args.Display);

        setup.Source.Resize(WIDTH * 2, HEIGHT + 2);
        setup.CaptureFrame(new ColorBGRA(1, 2, 3, 0));

        Assert.AreEqual((WIDTH * 2, HEIGHT + 2), (setup.Capture.Display.Width, setup.Capture.Display.Height));
        CollectionAssert.AreEqual(new[] { setup.Capture.Display }, changes);
        AssertPixels(zone, new ColorBGRA(1, 2, 3, 255));

        // Smaller than the zone: it doesn't fit anymore.
        setup.Source.Resize(WIDTH / 2, HEIGHT / 2);
        setup.CaptureFrame(new ColorBGRA(4, 5, 6, 0));

        Assert.AreEqual((WIDTH / 2, HEIGHT / 2), (setup.Capture.Display.Width, setup.Capture.Display.Height));
        Assert.IsFalse(zone.IsValid);
    }

    /// <summary>
    /// PipeWire frees the buffers of the old format with a new one: a zone written before the next frame must not read them.
    /// </summary>
    [TestMethod]
    public void ZonesArentReadFromRemovedBuffers()
    {
        using Setup setup = Setup.Start();
        setup.CaptureFrame(Color);

        setup.Source.Resize(WIDTH * 2, HEIGHT);
        Thread.Sleep(200); // the buffers are renegotiated

        CaptureZone<ColorBGRA> zone = setup.Capture.RegisterCaptureZone(new CaptureRegion(0, 0, 8, 8));
        Assert.AreEqual(CaptureResult.Timeout, setup.Capture.CaptureScreen());

        // Not written: the zone keeps its initial pixels.
        AssertPixels(zone, default);
    }

    /// <summary>
    /// Like a monitor the compositor stops sharing: the node goes away, and the stream is just paused.
    /// </summary>
    [TestMethod]
    public void RemovedNodeMakesTheCaptureUnavailable()
    {
        using Setup setup = Setup.Start();
        setup.CaptureFrame(Color);

        setup.Source.Dispose();

        // Then the restart to the node fails (PipeWire: "no target node available").
        List<string> errors = AssertBecomesUnavailable(setup.Capture);
        Assert.IsTrue(errors.Any(error => error.Contains("paused")), string.Join(Environment.NewLine, errors));
    }

    /// <summary>
    /// A compositor that offers neither BGRx nor BGRA (RGBA here): the stream can't start.
    /// </summary>
    [TestMethod]
    public void UnsupportedFormatMakesTheCaptureUnavailable()
    {
        using Setup setup = Setup.Start(videoFormat: 11); // SPA_VIDEO_FORMAT_RGBA

        // The first restart (by the constructor) failed already: PipeWire reports "no more input formats".
        Assert.Contains("formats", setup.Capture.LastError?.Message ?? string.Empty);
        AssertBecomesUnavailable(setup.Capture);
    }

    /// <summary>
    /// Like a PipeWire daemon that stops: the session counts as ended (its nodes are gone), the next enumeration starts a new one.
    /// </summary>
    [TestMethod]
    public void BrokenPipeWireConnectionEndsTheSession()
    {
        using Setup setup = Setup.Start();
        CaptureZone<ColorBGRA> zone = setup.Capture.RegisterCaptureZone(new CaptureRegion(0, 0, WIDTH, HEIGHT));
        setup.CaptureFrame(Color);

        setup.Portal.BreakPipeWireConnections();
        AssertBecomesUnavailable(setup.Capture);

        _ = setup.Service.GetDisplays(setup.Service.GetGraphicsCards().Single()).ToList();
        Assert.AreEqual(2, setup.Portal.CountCalls("CreateSession"));

        setup.CaptureFrame(new ColorBGRA(1, 2, 3, 0));
        AssertPixels(zone, new ColorBGRA(1, 2, 3, 255));
    }

    [TestMethod]
    public void TimestampIsThePresentationTime()
    {
        using Setup setup = Setup.Start();

        // Nanoseconds of CLOCK_MONOTONIC, which the Stopwatch uses on Linux.
        long pts = Stopwatch.GetTimestamp() - 1_000_000;
        long timestamp = 0;
        setup.Capture.Updated += (_, args) => timestamp = args.Timestamp;

        setup.CaptureFrame(Color, pts);

        Assert.AreEqual(PipeWireStream.ToStopwatchTimestamp(pts), timestamp);
    }

    [TestMethod]
    public void FrameWithoutPresentationTimeIsCapturedNow()
    {
        using Setup setup = Setup.Start();

        long timestamp = 0;
        setup.Capture.Updated += (_, args) => timestamp = args.Timestamp;

        long before = Stopwatch.GetTimestamp();
        setup.CaptureFrame(Color);

        Assert.IsGreaterThanOrEqualTo(before, timestamp);
    }

    /// <summary>
    /// The portal tells another size (or none): the display has the size of the stream from the start, also after a restart.
    /// </summary>
    [TestMethod]
    [DataRow(1920, 1080)]
    [DataRow(0, 0)]
    public void DisplayHasTheSizeOfTheStream(int portalWidth, int portalHeight)
    {
        using Setup setup = Setup.Start(portalWidth: portalWidth, portalHeight: portalHeight);
        Assert.AreEqual((WIDTH, HEIGHT), (setup.Capture.Display.Width, setup.Capture.Display.Height));
        Assert.AreEqual("stream:0", setup.Capture.Display.Id);

        setup.CaptureFrame(Color);

        List<Display> changes = [];
        setup.Capture.DisplayChanged += (_, args) => changes.Add(args.Display);

        setup.Capture.Restart();
        Assert.AreEqual((WIDTH, HEIGHT), (setup.Capture.Display.Width, setup.Capture.Display.Height));

        setup.CaptureFrame(Color);

        Assert.AreEqual((WIDTH, HEIGHT), (setup.Capture.Display.Width, setup.Capture.Display.Height));
        Assert.IsEmpty(changes, $"The display changed: {string.Join(", ", changes)}.");
    }

    [TestMethod]
    public void DisplayThatIsntSharedIsUnavailable()
    {
        using Setup setup = Setup.Start();

        WaylandScreenCapture capture = setup.Service.GetScreenCapture(setup.Capture.Display with { Id = "stream:9" });

        Assert.AreEqual(CaptureResult.Unavailable, capture.CaptureScreen());
        Assert.Contains("isn't shared", capture.LastError?.Message ?? string.Empty);
    }

    [TestMethod]
    public void EndedSessionMakesTheCaptureUnavailableUntilANewOneShares()
    {
        using Setup setup = Setup.Start();
        CaptureZone<ColorBGRA> zone = setup.Capture.RegisterCaptureZone(new CaptureRegion(0, 0, WIDTH, HEIGHT));
        setup.CaptureFrame(Color);

        setup.Portal.CloseSessions();
        Assert.IsTrue(SpinWait.SpinUntil(() => setup.Capture.CaptureScreen() == CaptureResult.Unavailable, WaitTimeout), "The capture didn't end with the session.");

        // The next enumeration starts a new session (without a dialog here); the capture restarts on it.
        _ = setup.Service.GetDisplays(setup.Service.GetGraphicsCards().Single()).ToList();
        Assert.AreEqual(2, setup.Portal.CountCalls("Start"));

        setup.CaptureFrame(new ColorBGRA(1, 2, 3, 0));
        AssertPixels(zone, new ColorBGRA(1, 2, 3, 255));
    }

    [TestMethod]
    public void DisposedServiceDisposesTheCaptures()
    {
        using Setup setup = Setup.Start();
        setup.CaptureFrame(Color);

        setup.Service.Dispose();

        Assert.ThrowsExactly<ObjectDisposedException>(() => setup.Capture.CaptureScreen());
    }

    // Returns the errors reported meanwhile.
    private static List<string> AssertBecomesUnavailable(WaylandScreenCapture capture)
    {
        List<string> errors = [];
        capture.ErrorOccurred += (_, args) => errors.Add(args.Exception.Message);

        List<CaptureResult> results = [];
        Assert.IsTrue(SpinWait.SpinUntil(() =>
        {
            results.Add(capture.CaptureScreen());
            return results[^1] == CaptureResult.Unavailable;
        }, TimeSpan.FromSeconds(10)), $"The capture didn't become unavailable: {string.Join(", ", results.Distinct())}.");
        Assert.IsNotNull(capture.LastError);

        return errors;
    }

    private static void AssertPixels(CaptureZone<ColorBGRA> zone, ColorBGRA expected)
    {
        using CaptureFrame<ColorBGRA> frame = zone.AcquireFrame();
        Assert.AreEqual(zone.Width * zone.Height, frame.Pixels.Length);
        foreach (ColorBGRA pixel in frame.Pixels)
            Assert.AreEqual(expected, pixel);
    }

    // A source, a portal sharing it and a capture of it.
    private sealed class Setup : IDisposable
    {
        public required PipeWireVideoSource Source { get; init; }
        public required FakeScreenCastPortal Portal { get; init; }
        public required WaylandScreenCaptureService Service { get; init; }
        public required WaylandScreenCapture Capture { get; init; }

        // The portal tells the size it gets (or none for 0).
        public static Setup Start(uint videoFormat = SPA_VIDEO_FORMAT_BGRx, int stride = 0, int portalWidth = WIDTH, int portalHeight = HEIGHT, (int Default, int Min, int Max)? buffers = null)
        {
            PipeWireVideoSource source = PipeWireVideoSource.Start(WIDTH, HEIGHT, videoFormat, stride, buffers);
            FakeScreenCastPortal? portal = null;
            WaylandScreenCaptureService? service = null;
            try
            {
                portal = FakeScreenCastPortal.Start();
                portal.Streams.Clear();
                portal.Streams.Add(new FakeStream(source.NodeId, Size: portalWidth > 0 ? (portalWidth, portalHeight) : null));

                service = new WaylandScreenCaptureService(new WaylandCaptureOptions { Timeout = TimeSpan.FromMilliseconds(100) });
                Display display = service.GetDisplays(service.GetGraphicsCards().Single()).Single();

                return new Setup { Source = source, Portal = portal, Service = service, Capture = service.GetScreenCapture(display) };
            }
            catch
            {
                service?.Dispose();
                portal?.Dispose();
                source.Dispose();
                throw;
            }
        }

        /// <summary>
        /// Sends frames until the capture got one: the first ones are lost while PipeWire links the capture to the source.
        /// </summary>
        public void CaptureFrame(ColorBGRA color, long pts = 0)
        {
            Stopwatch stopwatch = Stopwatch.StartNew();
            CaptureResult result;
            do
            {
                Source.Send(color, pts);
                result = Capture.CaptureScreen();
                if (result == CaptureResult.NewFrame) return;
            } while (stopwatch.Elapsed < WaitTimeout);

            Assert.Fail($"No frame was captured ({result}, {Source.SentFrames} sent, last error: {Capture.LastError?.Message}).");
        }

        public void Dispose()
        {
            Service.Dispose();
            Portal.Dispose();
            Source.Dispose();
        }
    }
}
