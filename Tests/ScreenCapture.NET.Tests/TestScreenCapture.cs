using System.Diagnostics;
using System.Runtime.InteropServices;
using HPPH;

namespace ScreenCapture.NET.Tests;

internal class TestScreenCapture : AbstractScreenCapture<ColorARGB>
{
    #region Properties & Fields

    /// <summary>
    /// Called at the start of every zone update (with the zone lock held).
    /// </summary>
    public Action<CaptureZone<ColorARGB>>? ZoneUpdating { get; set; }

    /// <summary>
    /// Gets or sets the color written to each pixel of a zone (by its location in the zone).
    /// </summary>
    public Func<int, int, ColorARGB> Pattern { get; set; } = GetColorFromLocation;

    /// <summary>
    /// Gets or sets if zone updates write their buffer. If <c>false</c>, they report that there was nothing to write.
    /// </summary>
    public bool WriteZones { get; set; } = true;

    /// <summary>
    /// Gets or sets the result of the next screen captures.
    /// </summary>
    public CaptureResult Result { get; set; } = CaptureResult.NewFrame;

    /// <summary>
    /// Gets or sets the timestamp reported with <see cref="CaptureResult.NewFrame"/> (0: the base takes the current time).
    /// </summary>
    public long Timestamp { get; set; }

    /// <summary>
    /// Gets or sets an exception thrown by the next screen captures instead of returning <see cref="Result"/>.
    /// </summary>
    public Exception? CaptureException { get; set; }

    /// <summary>
    /// Gets or sets an error reported (not thrown) by the next screen captures while they hold <see cref="CaptureLock"/>, like a backend that recovers from it.
    /// </summary>
    public Exception? ReportedCaptureError { get; set; }

    /// <summary>
    /// Gets the lock held by <see cref="PerformScreenCapture"/>, like the capture lock of a backend.
    /// </summary>
    public Lock CaptureLock { get; } = new();

    /// <summary>
    /// Gets or sets if the simulated backend runs. While it doesn't, captures restart it (with the backoff of the base class) instead of returning <see cref="Result"/>.
    /// </summary>
    public bool IsRunning { get; set; } = true;

    /// <summary>
    /// Gets or sets if restarts succeed (and make the capture run).
    /// </summary>
    public bool RestartSucceeds { get; set; } = true;

    /// <summary>
    /// Gets or sets an exception thrown by the next restarts.
    /// </summary>
    public Exception? RestartException { get; set; }

    /// <summary>
    /// Gets the times of all restart attempts, since the capture was created.
    /// </summary>
    public List<TimeSpan> RestartAttempts { get; } = [];

    /// <summary>
    /// Gets or sets the display the next successful restarts find, like a backend that reads its display again; <c>null</c> keeps the current one.
    /// </summary>
    public Display? NextDisplay { get; set; }

    /// <summary>
    /// Gets a snapshot of the registered zones.
    /// </summary>
    public IReadOnlyList<CaptureZone<ColorARGB>> Zones => CaptureZones;

    private readonly long _created = Stopwatch.GetTimestamp();

    #endregion

    #region Constructors

    public TestScreenCapture(ImageRotation rotation = ImageRotation.None)
        : base(CreateDisplay(1920, 1080, rotation))
    { }

    #endregion

    #region Methods

    /// <summary>
    /// Creates the display of the test capture with the given size (the same <see cref="Display.Id"/>).
    /// </summary>
    public static Display CreateDisplay(int width, int height, ImageRotation rotation = ImageRotation.None)
        => new("Test", 0, "Test", string.Empty, 0, 0, width, height, rotation, new GraphicsCard("Test", 0, "Test", 0, 0));

    protected override CaptureResult PerformScreenCapture(out long timestamp)
    {
        lock (CaptureLock)
        {
            timestamp = 0;
            if (!IsRunning) return RestartCapture();

            if (CaptureException != null) throw CaptureException;
            if (ReportedCaptureError != null) ReportError(ReportedCaptureError);

            timestamp = Timestamp;
            return Result;
        }
    }

    /// <summary>
    /// Reports an error outside of a capture, like a failed <see cref="AbstractScreenCapture{TColor}.Restart"/>.
    /// </summary>
    public void Report(Exception exception) => ReportError(exception);

    protected override bool PerformRestart()
    {
        lock (CaptureLock)
        {
            RestartAttempts.Add(Stopwatch.GetElapsedTime(_created));
            if (RestartException != null) throw RestartException;

            IsRunning = RestartSucceeds;
            if (IsRunning && (NextDisplay is { } display))
                SetDisplay(display);

            return IsRunning;
        }
    }

    // Under CaptureLock like a real backend (see the contract of PerformCaptureZoneUpdate): captures on other threads don't run meanwhile.
    protected override bool PerformCaptureZoneUpdate(CaptureZone<ColorARGB> captureZone, Span<byte> buffer)
    {
        lock (CaptureLock)
        {
            ZoneUpdating?.Invoke(captureZone);
            if (!WriteZones) return false;

            Func<int, int, ColorARGB> pattern = Pattern;

            Span<ColorARGB> pixels = MemoryMarshal.Cast<byte, ColorARGB>(buffer);

            int width = captureZone.Width;
            for (int y = 0; y < captureZone.Height; y++)
                for (int x = 0; x < width; x++)
                    pixels[(y * width) + x] = pattern(x, y);

            return true;
        }
    }

    /// <summary>
    /// Calls <see cref="AbstractScreenCapture{TColor}.WriteCaptureZone"/>.
    /// </summary>
    public static void Write(RefImage<ColorARGB> frame, CaptureZone<ColorARGB> captureZone, Span<byte> buffer, ImageRotation rotation = ImageRotation.None)
        => WriteCaptureZone(frame, captureZone, buffer, rotation);

    /// <summary>
    /// Calls <see cref="AbstractScreenCapture{TColor}.WriteCaptureZoneFromRegion"/>.
    /// </summary>
    public static void WriteFromRegion(RefImage<ColorARGB> region, CaptureZone<ColorARGB> captureZone, Span<byte> buffer, ImageRotation rotation = ImageRotation.None)
        => WriteCaptureZoneFromRegion(region, captureZone, buffer, rotation);

    /// <summary>
    /// Calls <see cref="AbstractScreenCapture{TColor}.WriteCaptureZoneFromScaled"/>.
    /// </summary>
    public static void WriteFromScaled(RefImage<ColorARGB> scaled, CaptureZone<ColorARGB> captureZone, Span<byte> buffer, ImageRotation rotation = ImageRotation.None)
        => WriteCaptureZoneFromScaled(scaled, captureZone, buffer, rotation);

    /// <summary>
    /// Calls <see cref="AbstractScreenCapture{TColor}.GetSourceRegion"/>.
    /// </summary>
    public static CaptureRegion SourceRegion(CaptureRegion region, Display display) => GetSourceRegion(region, display);

    public static ColorARGB GetColorFromLocation(int x, int y)
    {
        byte[] xBytes = BitConverter.GetBytes((short)x);
        byte[] yBytes = BitConverter.GetBytes((short)y);
        return new ColorARGB(xBytes[0], xBytes[1], yBytes[0], yBytes[1]);
    }

    #endregion
}