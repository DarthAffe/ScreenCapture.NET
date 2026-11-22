using System.Diagnostics;

namespace ScreenCapture.NET.Viewer;

/// <summary>
/// A running capture: drives it with a <see cref="ScreenCaptureLoop"/>, keeps a zone over the whole display for the preview and the zones the user added.
/// </summary>
/// <remarks>
/// The events of the capture are raised on the thread of the loop. The zones are added, changed and removed on the UI thread while the loop captures.
/// </remarks>
internal sealed class CaptureSession : IDisposable
{
    #region Properties & Fields

    // The preview is downscaled until it's at most this wide and high.
    private const int MAX_PREVIEW_WIDTH = 1920;
    private const int MAX_PREVIEW_HEIGHT = 1200;

    private readonly ScreenCaptureLoop _loop;
    private readonly List<ZoneMirror> _zones = [];

    // The statistics of the captures since the last TakeStats, guarded by _statsLock. Written on the thread of the loop.
    private readonly Lock _statsLock = new();
    private readonly int[] _results = new int[Enum.GetValues<CaptureResult>().Length];
    private readonly long[] _totalResults = new long[Enum.GetValues<CaptureResult>().Length];
    private long _frameNumber;
    private TimeSpan _minAge = TimeSpan.MaxValue;
    private TimeSpan _maxAge = TimeSpan.MinValue;
    private TimeSpan _sumAge;
    private int _ages;
    private long _lastCaptureEnd;
    private TimeSpan _maxInterval;
    private TimeSpan _sumInterval;
    private int _intervals;

    // Whether the last capture worked (NewFrame, NoChange or Timeout), to log only the changes.
    private bool? _isWorking;

    public IScreenCapture ScreenCapture { get; }

    /// <summary>
    /// Gets the zone over the whole display that the preview shows.
    /// </summary>
    public ZoneMirror Preview { get; }

    /// <summary>
    /// Gets the zones the user added.
    /// </summary>
    public IReadOnlyList<ZoneMirror> Zones => _zones;

    /// <summary>
    /// Occurs on the thread of the loop when the preview or a zone has a new frame.
    /// </summary>
    public event Action? FrameReady;

    /// <summary>
    /// Occurs on the thread of the loop when the display changed (the preview zone already follows it).
    /// </summary>
    public event Action<Display>? DisplayChanged;

    /// <summary>
    /// Occurs on the thread of the loop for every error of the capture.
    /// </summary>
    public event Action<Exception>? ErrorOccurred;

    /// <summary>
    /// Occurs on the thread of the loop for things worth a line in the log (results that stop or start working, restarts, display changes).
    /// </summary>
    public event Action<string>? Log;

    /// <summary>
    /// Gets or sets the target frame rate of the loop (<c>null</c> for no limit).
    /// </summary>
    public double? TargetFrameRate
    {
        get => _loop.TargetFrameRate;
        set => _loop.TargetFrameRate = value;
    }

    #endregion

    #region Constructors

    public CaptureSession(IScreenCapture screenCapture, double? targetFrameRate)
    {
        ScreenCapture = screenCapture;

        Display display = screenCapture.Display;
        Preview = new ZoneMirror(screenCapture.RegisterCaptureZone(WholeDisplay(display), PreviewLevel(display)));
        Preview.FrameReady += OnFrameReady;

        screenCapture.DisplayChanged += OnDisplayChanged;
        screenCapture.Updated += OnUpdated;
        screenCapture.ErrorOccurred += OnErrorOccurred;

        _loop = new ScreenCaptureLoop(screenCapture, targetFrameRate);
        _loop.Start();
    }

    #endregion

    #region Methods

    /// <summary>
    /// Gets the statistics of the captures since the last call and starts new ones.
    /// </summary>
    public CaptureStats TakeStats()
    {
        lock (_statsLock)
        {
            CaptureStats stats = new([.. _results], [.. _totalResults], _frameNumber,
                                     _ages > 0 ? _minAge : null, _ages > 0 ? _sumAge / _ages : null, _ages > 0 ? _maxAge : null,
                                     _intervals > 0 ? _sumInterval / _intervals : null, _intervals > 0 ? _maxInterval : null);

            Array.Clear(_results);
            _minAge = TimeSpan.MaxValue;
            _maxAge = TimeSpan.MinValue;
            _sumAge = TimeSpan.Zero;
            _ages = 0;
            _maxInterval = TimeSpan.Zero;
            _sumInterval = TimeSpan.Zero;
            _intervals = 0;
            return stats;
        }
    }

    /// <summary>
    /// Restarts the capture (it acquires its source again on the next capture).
    /// </summary>
    public void Restart()
    {
        Log?.Invoke("Restart requested");
        ScreenCapture.Restart();
    }

    public ZoneMirror AddZone(CaptureRegion region, int downscaleLevel)
    {
        ZoneMirror zone = new(ScreenCapture.RegisterCaptureZone(region, downscaleLevel));
        zone.FrameReady += OnFrameReady;
        _zones.Add(zone);
        return zone;
    }

    public void UpdateZone(ZoneMirror zone, CaptureRegion? region = null, int? downscaleLevel = null)
        => ScreenCapture.UpdateCaptureZone(zone.Zone, region, downscaleLevel);

    public void RemoveZone(ZoneMirror zone)
    {
        if (!_zones.Remove(zone)) return;

        ScreenCapture.UnregisterCaptureZone(zone.Zone);
        zone.Dispose();
    }

    private void OnFrameReady() => FrameReady?.Invoke();

    // The preview zone follows the display, e.g. after a resolution change or for a resized window. The zones of the user stay as they are (and may be invalid now).
    private void OnDisplayChanged(object? sender, ScreenCaptureDisplayChangedEventArgs e)
    {
        ScreenCapture.UpdateCaptureZone(Preview.Zone, WholeDisplay(e.Display), PreviewLevel(e.Display));
        Log?.Invoke($"Display changed: {Backends.Describe(e.PreviousDisplay)} → {Backends.Describe(e.Display)}");
        DisplayChanged?.Invoke(e.Display);
    }

    private void OnUpdated(object? sender, ScreenCaptureUpdatedEventArgs e)
    {
        long now = Stopwatch.GetTimestamp();

        lock (_statsLock)
        {
            _results[(int)e.Result]++;
            _totalResults[(int)e.Result]++;
            _frameNumber = e.FrameNumber;

            // How long ago the frame was shown when the capture returned it (WGC's timestamp is when it will be shown, so it's negative there).
            if ((e.Result == CaptureResult.NewFrame) && (e.Timestamp != 0))
            {
                TimeSpan age = Stopwatch.GetElapsedTime(e.Timestamp, now);
                _minAge = age < _minAge ? age : _minAge;
                _maxAge = age > _maxAge ? age : _maxAge;
                _sumAge += age;
                _ages++;
            }

            if (_lastCaptureEnd != 0)
            {
                TimeSpan interval = Stopwatch.GetElapsedTime(_lastCaptureEnd, now);
                _maxInterval = interval > _maxInterval ? interval : _maxInterval;
                _sumInterval += interval;
                _intervals++;
            }

            _lastCaptureEnd = now;
        }

        // NewFrame, NoChange and Timeout alternate on a still screen: only the changes between working and not working are logged, and every restart.
        bool isWorking = e.Result is CaptureResult.NewFrame or CaptureResult.NoChange or CaptureResult.Timeout;
        if ((isWorking != _isWorking) || (e.Result == CaptureResult.Restarting))
        {
            _isWorking = isWorking;
            Log?.Invoke(isWorking ? $"Capturing ({e.Result})" : $"{e.Result}: {ScreenCapture.LastError?.Message}");
        }
    }

    private void OnErrorOccurred(object? sender, ScreenCaptureErrorEventArgs e)
    {
        Log?.Invoke($"Error: {e.Exception.GetType().Name}: {e.Exception.Message}");
        ErrorOccurred?.Invoke(e.Exception);
    }

    private static CaptureRegion WholeDisplay(Display display) => new(0, 0, display.Width, display.Height);

    private static int PreviewLevel(Display display)
    {
        int level = 0;
        while (((display.Width >> level) > MAX_PREVIEW_WIDTH) || ((display.Height >> level) > MAX_PREVIEW_HEIGHT))
            level++;

        return level;
    }

    /// <summary>
    /// Stops the loop and disposes the capture (it waits for a running capture).
    /// </summary>
    public void Dispose()
    {
        _loop.Dispose();
        ScreenCapture.Dispose();
    }

    #endregion
}

/// <summary>
/// The statistics of the captures of a period (see <see cref="CaptureSession.TakeStats"/>).
/// </summary>
/// <param name="Results">The number of captures per <see cref="CaptureResult"/> in the period.</param>
/// <param name="TotalResults">The number of captures per <see cref="CaptureResult"/> since the session started.</param>
/// <param name="FrameNumber">The frame number of the last capture.</param>
/// <param name="MinAge">The youngest frame age (time since the frame's timestamp when the capture returned), or <c>null</c> without new frames.</param>
/// <param name="AverageAge">The average frame age.</param>
/// <param name="MaxAge">The oldest frame age.</param>
/// <param name="AverageInterval">The average time between the ends of two captures, or <c>null</c> with less than two captures.</param>
/// <param name="MaxInterval">The longest time between the ends of two captures.</param>
internal sealed record CaptureStats(int[] Results, long[] TotalResults, long FrameNumber,
                                   TimeSpan? MinAge, TimeSpan? AverageAge, TimeSpan? MaxAge,
                                   TimeSpan? AverageInterval, TimeSpan? MaxInterval);
