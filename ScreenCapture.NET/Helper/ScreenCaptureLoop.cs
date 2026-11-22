using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace ScreenCapture.NET;

/// <summary>
/// Captures a screen continuously on a background thread, at a target frame rate.
/// </summary>
/// <remarks>
/// The results arrive through the events of the capture and its zones, raised on the thread of the loop.
/// <para>
/// After <see cref="CaptureResult.Failed"/> the loop waits 100 ms, doubling up to 2 s. Restarts are handled by the capture itself.
/// </para>
/// <para>
/// Disposing the loop doesn't dispose the capture. Disposing the capture ends the loop.
/// All members are thread-safe. For <c>async</c> code, see <see cref="ScreenCaptureExtensions.CaptureLoopAsync"/>.
/// </para>
/// </remarks>
public sealed class ScreenCaptureLoop : IDisposable
{
    #region Properties & Fields

    // Never held while capturing or waiting.
    private readonly Lock _lock = new();

    private double? _targetFrameRate;
    private TimeSpan _interval;

    // null once stopped or ended.
    private LoopRun? _run;

    // Running or not.
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
    /// Measured from start to start. The OS timer is coarse (about 15.6 ms on Windows), so single captures can start late, but the average rate is kept.
    /// <para>
    /// Without a limit DX11, WGC and Wayland are paced by the display, since they wait for the next frame.
    /// DX9 and X11 return right away and keep a CPU core busy.
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
    /// Gets a value indicating whether the loop runs.
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
    /// Completes successfully when the loop was stopped or the capture was disposed.
    /// Faults if <see cref="IScreenCapture.CaptureScreen"/> threw anything else, which ends the loop.
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
    /// Initializes a new instance of the <see cref="ScreenCaptureLoop"/> class. It doesn't run until <see cref="Start"/> is called.
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
    /// Can be started again after it stopped or ended. Captures of the old and the new run never overlap.
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
    /// The loop ends after the current capture, which can take up to the timeout of the capture or its restart backoff (up to 2 s).
    /// Called from an event handler on the thread of the loop, it doesn't wait; the loop ends when the handler returned.
    /// </remarks>
    public void Stop()
    {
        LoopRun? run = RequestStop();

        // Waiting for a run that waits for this thread would deadlock.
        if ((run != null) && (run.Thread != Thread.CurrentThread) && !run.WaitsFor(Thread.CurrentThread))
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
                    // Ends early on Stop or a changed frame rate.
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

    // One run of the loop, from Start until it ended.
    private sealed class LoopRun(LoopRun? previous)
    {
        #region Properties & Fields

        // Guards _stopRequested and _woken, pulsed to end waits early.
        private readonly object _signal = new();

        private bool _stopRequested;
        private bool _woken;

        // Its thread may still finish a capture. Cleared once it ended, so runs don't keep each other alive.
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
            Volatile.Read(ref _previous)?.Thread.Join();
            Volatile.Write(ref _previous, null);
        }

        // If the thread belongs to an earlier run this one still waits for.
        public bool WaitsFor(Thread thread)
        {
            for (LoopRun? run = Volatile.Read(ref _previous); run != null; run = Volatile.Read(ref run._previous))
                if (run.Thread == thread)
                    return true;

            return false;
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
