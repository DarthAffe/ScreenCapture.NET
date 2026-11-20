using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace ScreenCapture.NET;

/// <summary>
/// Captures a screen continuously on a background thread, at a target frame rate.
/// </summary>
/// <remarks>
/// The loop calls <see cref="IScreenCapture.CaptureScreen"/> over and over; the results arrive through the events of the capture and its zones
/// (<see cref="IScreenCapture.Updated"/>, <see cref="ICaptureZone.Updated"/>), which are raised on the thread of the loop.
/// <para>
/// Restarts and their backoff are handled by the capture (see <see cref="IScreenCapture.CaptureScreen"/>).
/// The loop only adds a delay after <see cref="CaptureResult.Failed"/>: 100 ms, doubling with every further failure up to 2 s,
/// so a capture that keeps failing doesn't flood <see cref="IScreenCapture.ErrorOccurred"/>.
/// </para>
/// <para>
/// The loop doesn't own the capture: disposing the loop stops it, but doesn't dispose the capture. Disposing the capture ends the loop.
/// All members are thread-safe. For an <c>async</c> consumer, see <see cref="ScreenCaptureExtensions.CaptureLoopAsync"/>.
/// </para>
/// </remarks>
public sealed class ScreenCaptureLoop : IDisposable
{
    #region Properties & Fields

    // Guards _targetFrameRate, _interval, _run, _lastRun and _isDisposed. Never held while capturing or waiting.
    private readonly Lock _lock = new();

    private double? _targetFrameRate;
    private TimeSpan _interval;

    // The run Start started and Stop didn't stop yet; null after the loop ended by itself.
    private LoopRun? _run;

    // The run started last (running or not).
    private LoopRun? _lastRun;

    private bool _isDisposed;

    /// <summary>
    /// Gets the capture this loop drives.
    /// </summary>
    public IScreenCapture ScreenCapture { get; }

    /// <summary>
    /// Gets or sets the number of captures per second the loop aims for, or <c>null</c> to capture again as soon as the last capture returned.
    /// </summary>
    /// <remarks>
    /// The rate is measured from the start of one capture to the start of the next. The loop waits with the OS timer, which is coarse (e.g. about 15.6 ms on Windows):
    /// single captures can start late, but the following ones start sooner, so the average rate is kept as long as the capture is fast enough.
    /// <para>
    /// Without a limit, DX11 captures are paced by the display: a capture waits for the next frame (up to its timeout).
    /// DX9 and X11 captures return right away, so without a limit the loop keeps a CPU core busy.
    /// </para>
    /// A change applies to the next capture, also while the loop is waiting.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The value isn't a finite number greater than 0, or is less than one frame per day.</exception>
    public double? TargetFrameRate
    {
        get
        {
            lock (_lock)
                return _targetFrameRate;
        }
        set
        {
            TimeSpan interval = CapturePacer.GetInterval(value, nameof(value));

            LoopRun? run;
            lock (_lock)
            {
                _targetFrameRate = value;
                _interval = interval;
                run = _run;
            }

            run?.Wake();
        }
    }

    /// <summary>
    /// Gets a value indicating whether the loop runs: it was started and neither stopped nor ended (see <see cref="Completion"/>).
    /// </summary>
    public bool IsRunning
    {
        get
        {
            lock (_lock)
                return _run != null;
        }
    }

    /// <summary>
    /// Gets a task that completes when the loop started last has ended (a completed task if it was never started).
    /// </summary>
    /// <remarks>
    /// The task completes successfully when the loop was stopped or the capture was disposed.
    /// It faults with the exception if <see cref="IScreenCapture.CaptureScreen"/> threw anything else (which a capture is not supposed to do); the loop ends then.
    /// </remarks>
    public Task Completion
    {
        get
        {
            lock (_lock)
                return _lastRun?.Completion ?? Task.CompletedTask;
        }
    }

    #endregion

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="ScreenCaptureLoop"/> class. The loop doesn't run until it's started (see <see cref="Start"/>).
    /// </summary>
    /// <param name="screenCapture">The capture to drive.</param>
    /// <param name="targetFrameRate">The number of captures per second the loop aims for, or <c>null</c> for no limit (see <see cref="TargetFrameRate"/>).</param>
    /// <exception cref="ArgumentNullException"><paramref name="screenCapture"/> is <c>null</c>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="targetFrameRate"/> isn't a finite number greater than 0, or is less than one frame per day.</exception>
    public ScreenCaptureLoop(IScreenCapture screenCapture, double? targetFrameRate = null)
    {
        ArgumentNullException.ThrowIfNull(screenCapture);

        this._targetFrameRate = targetFrameRate;
        this.ScreenCapture = screenCapture;

        _interval = CapturePacer.GetInterval(targetFrameRate, nameof(targetFrameRate));
    }

    #endregion

    #region Methods

    /// <summary>
    /// Starts the loop on a new background thread. Does nothing if it already runs.
    /// </summary>
    /// <remarks>
    /// The loop can be started again after it stopped or ended. A loop that is still finishing a capture after <see cref="Stop"/> was called on its own thread
    /// finishes it before the new one captures, so captures of this loop never overlap.
    /// </remarks>
    /// <exception cref="ObjectDisposedException">The loop is disposed.</exception>
    public void Start()
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_isDisposed, this);
            if (_run != null) return;

            LoopRun run = new(_lastRun);
            Thread thread = new(() => Run(run))
            {
                IsBackground = true,
                Name = $"ScreenCaptureLoop ({ScreenCapture.Display.DeviceName})"
            };
            run.Thread = thread;

            _run = _lastRun = run;
            thread.Start();
        }
    }

    /// <summary>
    /// Stops the loop and waits until it has ended. Does nothing if it doesn't run.
    /// </summary>
    /// <remarks>
    /// The loop ends after the current capture, which can take up to the timeout of the capture (DX11) or its restart backoff (up to 2 s).
    /// Called on the thread of the loop (e.g. from an <see cref="IScreenCapture.Updated"/> handler) it doesn't wait: the loop ends when the handler returned.
    /// </remarks>
    public void Stop()
    {
        LoopRun? run = RequestStop();

        if ((run != null) && (run.Thread != Thread.CurrentThread))
            run.Thread.Join();
    }

    /// <summary>
    /// Stops the loop without waiting for it. Does nothing if it doesn't run.
    /// </summary>
    /// <returns>A task that completes when the loop has ended (see <see cref="Completion"/>).</returns>
    public Task StopAsync() => RequestStop()?.Completion ?? Task.CompletedTask;

    private LoopRun? RequestStop()
    {
        LoopRun? run;
        lock (_lock)
        {
            run = _lastRun;
            _run = null;
        }

        run?.RequestStop();
        return run;
    }

    /// <summary>
    /// Stops the loop and waits until it has ended (see <see cref="Stop"/>). The capture isn't disposed.
    /// </summary>
    public void Dispose()
    {
        lock (_lock)
            _isDisposed = true;

        Stop();
    }

    private void Run(LoopRun run)
    {
        try
        {
            run.JoinPrevious();

            CapturePacer pacer = new();
            while (!run.IsStopRequested)
            {
                TimeSpan interval;
                lock (_lock)
                    interval = _interval;

                TimeSpan delay = pacer.GetDelay(Stopwatch.GetTimestamp(), interval);
                if (delay > TimeSpan.Zero)
                {
                    // Ends early on Stop or a changed frame rate; either way, check again.
                    run.Wait(delay);
                    continue;
                }

                long started = Stopwatch.GetTimestamp();
                if (TryCaptureScreen(ScreenCapture) is not { } result) break;

                pacer.Captured(started, Stopwatch.GetTimestamp(), result, interval);
            }

            End(run, null);
        }
        catch (Exception ex)
        {
            End(run, ex);
        }
    }

    private void End(LoopRun run, Exception? exception)
    {
        lock (_lock)
            if (_run == run)
                _run = null;

        if (exception == null)
            run.CompletionSource.TrySetResult();
        else
            run.CompletionSource.TrySetException(exception);
    }

    /// <summary>
    /// Captures the screen.
    /// </summary>
    /// <param name="screenCapture">The capture.</param>
    /// <returns>The result of the capture, or <c>null</c> if the capture is disposed.</returns>
    internal static CaptureResult? TryCaptureScreen(IScreenCapture screenCapture)
    {
        try
        {
            return screenCapture.CaptureScreen();
        }
        catch (ObjectDisposedException) when (screenCapture.IsDisposed)
        {
            return null;
        }
    }

    #endregion

    // One Start until the loop ended: its thread, the stop request and the signal that ends its waits.
    private sealed class LoopRun(LoopRun? previous)
    {
        #region Properties & Fields

        // Guards _stopRequested and _woken; waited on and pulsed to end waits early.
        private readonly object _signal = new();

        private bool _stopRequested;
        private bool _woken;

        // The run before this one, whose thread may still finish a capture (cleared once it has ended, so runs don't keep each other alive).
        private LoopRun? _previous = previous;

        public Thread Thread { get; set; } = null!;

        public TaskCompletionSource CompletionSource { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Completion => CompletionSource.Task;

        public bool IsStopRequested
        {
            get
            {
                lock (_signal)
                    return _stopRequested;
            }
        }

        #endregion

        #region Methods

        public void JoinPrevious()
        {
            _previous?.Thread.Join();
            _previous = null;
        }

        public void Wait(TimeSpan timeout)
        {
            lock (_signal)
            {
                if (!_stopRequested && !_woken)
                    Monitor.Wait(_signal, CapturePacer.ToWaitMilliseconds(timeout));

                _woken = false;
            }
        }

        public void Wake()
        {
            lock (_signal)
            {
                _woken = true;
                Monitor.PulseAll(_signal);
            }
        }

        public void RequestStop()
        {
            lock (_signal)
            {
                _stopRequested = true;
                Monitor.PulseAll(_signal);
            }
        }

        #endregion
    }
}
