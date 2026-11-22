using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using HPPH;

namespace ScreenCapture.NET;

/// <inheritdoc />
/// <remarks>
/// Thread-safe (see <see cref="IScreenCapture"/>). How backends guard their resources is described on
/// <see cref="PerformScreenCapture"/>, <see cref="PerformCaptureZoneUpdate"/> and <see cref="PerformRestart"/>.
/// <para>
/// Lock order: a zone lock is always taken before any lock of the screen capture, never while holding one.
/// </para>
/// </remarks>
public abstract class AbstractScreenCapture<TColor> : IScreenCapture
    where TColor : unmanaged, IColor<TColor>
{
    #region Constants

    // Up to this many zones the "updated" flags are on the stack.
    private const int UPDATED_STACKALLOC_THRESHOLD = 32;

    private const string NOT_REGISTERED_MESSAGE = "The capture zone is not registered to this screen capture.";

    // Errors not raised yet; beyond this the oldest are dropped.
    private const int MAX_PENDING_ERRORS = 16;

    private static readonly TimeSpan RESTART_WAIT_SLICE = TimeSpan.FromMilliseconds(50);

    #endregion

    #region Properties & Fields

    private bool _isDisposed;

    // Makes a second Dispose wait for the first. Taken before any other lock.
    private readonly Lock _disposeLock = new();

    /// <inheritdoc />
    /// <remarks>
    /// Set before the backend releases its resources.
    /// </remarks>
    public bool IsDisposed => Volatile.Read(ref _isDisposed);

    private Exception? _lastError;

    // Leaf lock: errors can be reported while holding any other lock.
    private readonly Lock _errorLock = new();
    private readonly Queue<Exception> _pendingErrors = new();

    // Leaf lock for _frameNumber, _frameTimestamp and _hasFrame.
    private readonly Lock _frameLock = new();
    private long _frameNumber;
    private long _frameTimestamp;

    private bool _hasFrame;

    // Leaf lock for the backoff, never held while restarting.
    private readonly Lock _restartLock = new();
    private readonly Backoff _restartBackoff = new();

    // Guards the zone set and the zone regions. Taken after a zone lock, never before.
    private readonly Lock _captureZonesLock = new();

    // Copy-on-write, so captures can walk it without a lock.
    private CaptureZone<TColor>[] _captureZones = [];

    // Leaf lock for the display changes, so backends can set the display while holding their locks. _display is read without it.
    private readonly Lock _displayLock = new();

    // Boxed, so it's replaced atomically.
    private DisplayBox _display;

    // The zones are fitted to the display up to _appliedDisplayVersion.
    private int _displayVersion;
    private int _appliedDisplayVersion;

    private ScreenCaptureDisplayChangedEventArgs? _pendingDisplayChange;

    /// <summary>
    /// Gets a snapshot of the <see cref="CaptureZone{TColor}"/> registered on this ScreenCapture.
    /// </summary>
    // ReSharper disable once InconsistentlySynchronizedField
    protected IReadOnlyList<CaptureZone<TColor>> CaptureZones => Volatile.Read(ref _captureZones);

    /// <inheritdoc />
    /// <remarks>
    /// Backends change it with <see cref="SetDisplay"/>.
    /// </remarks>
    public Display Display => Volatile.Read(ref _display).Display;

    /// <inheritdoc />
    public Exception? LastError => Volatile.Read(ref _lastError);

    #endregion

    #region Events

    /// <inheritdoc />
    public event EventHandler<ScreenCaptureUpdatedEventArgs>? Updated;

    /// <inheritdoc />
    public event EventHandler<ScreenCaptureErrorEventArgs>? ErrorOccurred;

    /// <inheritdoc />
    public event EventHandler<ScreenCaptureDisplayChangedEventArgs>? DisplayChanged;

    #endregion

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="AbstractScreenCapture{T}"/> class.
    /// </summary>
    /// <param name="display">The <see cref="Display"/> to duplicate.</param>
    protected AbstractScreenCapture(Display display)
    {
        _display = new DisplayBox(display);
    }

    #endregion

    #region Methods

    /// <inheritdoc />
    public CaptureResult CaptureScreen()
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        WaitForRestartAttempt();

        CaptureResult result;
        long timestamp;

        try
        {
            result = PerformScreenCapture(out timestamp);
        }
        catch (Exception ex)
        {
            ReportError(ex);
            result = CaptureResult.Failed;
            timestamp = 0;
        }

        ApplyDisplayChange();
        RaisePendingDisplayChange();

        long frameNumber;
        bool hasFrame;
        lock (_frameLock)
        {
            switch (result)
            {
                case CaptureResult.NewFrame:
                    _frameNumber++;
                    _frameTimestamp = timestamp != 0 ? timestamp : Stopwatch.GetTimestamp();
                    _hasFrame = true;
                    break;

                case CaptureResult.NoChange:
                case CaptureResult.Timeout:
                    break;

                default:
                    _hasFrame = false;
                    break;
            }

            frameNumber = _frameNumber;
            timestamp = _frameTimestamp;
            hasFrame = _hasFrame;
        }

        // ReSharper disable once InconsistentlySynchronizedField
        CaptureZone<TColor>[] captureZones = Volatile.Read(ref _captureZones);
        if (hasFrame && (captureZones.Length > 0))
        {
            // If an update throws, the rented array isn't returned. The pool tolerates that.
            bool[]? rented = null;
            Span<bool> updated = captureZones.Length <= UPDATED_STACKALLOC_THRESHOLD
                                     ? stackalloc bool[UPDATED_STACKALLOC_THRESHOLD]
                                     : (rented = ArrayPool<bool>.Shared.Rent(captureZones.Length));

            for (int i = 0; i < captureZones.Length; i++)
                updated[i] = TryUpdateCaptureZone(captureZones[i], frameNumber);

            // Raised once all zones are written, without holding a lock.
            for (int i = 0; i < captureZones.Length; i++)
                if (updated[i])
                    captureZones[i].RaiseUpdated();

            if (rented != null)
                ArrayPool<bool>.Shared.Return(rented);
        }

        RaisePendingErrors();

        OnUpdated(new ScreenCaptureUpdatedEventArgs(result, frameNumber, timestamp));

        return result;
    }

    // Not raised where they occur: the backends often hold their locks there, a handler locking a zone could deadlock.
    private void RaisePendingErrors()
    {
        while (true)
        {
            Exception? error;
            lock (_errorLock)
                if (!_pendingErrors.TryDequeue(out error))
                    return;

            OnErrorOccurred(new ScreenCaptureErrorEventArgs(error));
        }
    }

    /// <summary>
    /// Sets the display the capture duplicates, e.g. after a restart found it changed.
    /// </summary>
    /// <remarks>
    /// Call it once the resources of the backend match the new display. Can be called while holding any lock.
    /// The zones are fitted to it before they're written next; zones that don't fit anymore become invalid.
    /// <see cref="DisplayChanged"/> is raised by the next <see cref="CaptureScreen"/>.
    /// </remarks>
    /// <param name="display">The current display.</param>
    /// <exception cref="ArgumentException"><paramref name="display"/> has another <see cref="Display.Id"/>.</exception>
    protected void SetDisplay(Display display)
    {
        lock (_displayLock)
        {
            Display current = Display;
            if (display == current) return;

            if (!string.Equals(display.Id, current.Id, StringComparison.Ordinal))
                throw new ArgumentException($"The display '{display.Id}' isn't the display '{current.Id}' of this capture.", nameof(display));

            Volatile.Write(ref _display, new DisplayBox(display));
            _displayVersion++;

            // Changes since the last event are raised as one.
            Display previous = _pendingDisplayChange?.PreviousDisplay ?? current;
            _pendingDisplayChange = previous == display ? null : new ScreenCaptureDisplayChangedEventArgs(previous, display);
        }
    }

    private void ApplyDisplayChange()
    {
        int version;
        lock (_displayLock)
        {
            version = _displayVersion;
            if (version == _appliedDisplayVersion) return;
        }

        // Read under the lock to wait for a running register, which may have created its zone with the old display.
        CaptureZone<TColor>[] captureZones;
        lock (_captureZonesLock)
            captureZones = _captureZones;

        foreach (CaptureZone<TColor> captureZone in captureZones)
            using (captureZone.EnterUpdateScope())
                lock (_captureZonesLock)
                    captureZone.SetDisplay(Display);

        lock (_displayLock)
            if ((version - _appliedDisplayVersion) > 0)
                _appliedDisplayVersion = version;
    }

    private void RaisePendingDisplayChange()
    {
        ScreenCaptureDisplayChangedEventArgs? args;
        lock (_displayLock)
        {
            args = _pendingDisplayChange;
            _pendingDisplayChange = null;
        }

        if (args != null)
            OnDisplayChanged(args.Value);
    }

    /// <summary>
    /// Reports an error without throwing it.
    /// </summary>
    /// <remarks>
    /// Sets <see cref="LastError"/> right away, <see cref="ErrorOccurred"/> is raised by the next <see cref="CaptureScreen"/>. Can be called while holding any lock.
    /// Exceptions thrown by <see cref="PerformScreenCapture"/> and <see cref="PerformCaptureZoneUpdate"/> are reported automatically.
    /// </remarks>
    /// <param name="exception">The error.</param>
    protected void ReportError(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        Volatile.Write(ref _lastError, exception);

        lock (_errorLock)
        {
            if (_pendingErrors.Count >= MAX_PENDING_ERRORS)
                _pendingErrors.Dequeue();

            _pendingErrors.Enqueue(exception);
        }
    }

    private bool TryUpdateCaptureZone(CaptureZone<TColor> captureZone, long frameNumber)
    {
        if (!captureZone.NeedsUpdate(frameNumber)) return false;

        try
        {
            using (captureZone.EnterUpdateScope())
            {
                // Checked again: it can have been unregistered or written by another capture meanwhile.
                if (captureZone.IsUnregistered || !captureZone.NeedsUpdate(frameNumber)) return false;

                ZoneBuffer<TColor>? buffer = captureZone.BeginWrite();
                if (buffer == null) return false;

                bool requested = captureZone.TakeUpdateRequest();
                bool written = false;
                try
                {
                    written = PerformCaptureZoneUpdate(captureZone, buffer.Data);
                }
                finally
                {
                    captureZone.EndWrite(buffer, written);

                    // Not written: keep the request for the next capture.
                    if (!written && requested)
                        captureZone.RequestUpdate();
                }

                if (!written) return false;

                captureZone.SetWritten(frameNumber);
            }

            return true;
        }
        catch (Exception ex)
        {
            ReportError(ex);
            return false;
        }
    }

    /// <summary>
    /// Performs the actual screen capture.
    /// </summary>
    /// <remarks>
    /// Exceptions are reported and returned as <see cref="CaptureResult.Failed"/>. Report errors the capture recovers from with <see cref="ReportError"/>.
    /// <para>
    /// Can run on several threads at once and concurrently with <see cref="PerformCaptureZoneUpdate"/> and <see cref="PerformRestart"/>.
    /// Guard the backend's resources with one lock all three use, and never take a zone lock while holding it.
    /// </para>
    /// </remarks>
    /// <param name="timestamp">
    /// For <see cref="CaptureResult.NewFrame"/>: when the frame was shown as a <see cref="Stopwatch"/> timestamp, or 0 to use the current time.
    /// </param>
    /// <returns><see cref="CaptureResult.NewFrame"/> if a new frame was captured; otherwise why not.</returns>
    protected abstract CaptureResult PerformScreenCapture(out long timestamp);

    /// <summary>
    /// Performs an update of the given capture zone.
    /// </summary>
    /// <remarks>
    /// Only called for valid, registered zones, with the zone lock held, so the zone doesn't change meanwhile.
    /// Use <see cref="CaptureZoneWriter"/> to write zones in software.
    /// <para>
    /// Can run concurrently with <see cref="PerformScreenCapture"/> and <see cref="PerformRestart"/>: read the frame under the lock they use.
    /// A restart on another thread can change the display or drop the frame after this capture got it.
    /// Check that the backend holds a frame and that the region lies within it (<see cref="CaptureRegion.IsWithin"/>), and return <c>false</c> if not.
    /// </para>
    /// <para>
    /// Also called after <see cref="CaptureResult.NoChange"/> and <see cref="CaptureResult.Timeout"/> for zones that don't show the last frame yet,
    /// so the backend has to keep it.
    /// </para>
    /// </remarks>
    /// <param name="captureZone">The capture zone to update.</param>
    /// <param name="buffer">The back buffer of the zone, with its current size and no padding. Only shown if this returns <c>true</c>.</param>
    /// <returns><c>true</c> if <paramref name="buffer"/> was filled; <c>false</c> if there was nothing to write.</returns>
    protected abstract bool PerformCaptureZoneUpdate(CaptureZone<TColor> captureZone, Span<TColor> buffer);

    /// <summary>
    /// Raises the <see cref="Updated"/>-event.
    /// </summary>
    /// <param name="args">The result of the capture and the latest frame.</param>
    protected virtual void OnUpdated(ScreenCaptureUpdatedEventArgs args)
    {
        try
        {
            Updated?.Invoke(this, args);
        }
        catch { /* Exceptions of handlers aren't errors of the capture. */ }
    }

    /// <summary>
    /// Raises the <see cref="ErrorOccurred"/>-event.
    /// </summary>
    /// <param name="args">The error.</param>
    protected virtual void OnErrorOccurred(ScreenCaptureErrorEventArgs args)
    {
        try
        {
            ErrorOccurred?.Invoke(this, args);
        }
        catch { /* Reporting it would raise the event again. */ }
    }

    /// <summary>
    /// Raises the <see cref="DisplayChanged"/>-event.
    /// </summary>
    /// <param name="args">The display before and after the change.</param>
    protected virtual void OnDisplayChanged(ScreenCaptureDisplayChangedEventArgs args)
    {
        try
        {
            DisplayChanged?.Invoke(this, args);
        }
        catch { /* Exceptions of handlers aren't errors of the capture. */ }
    }

    /// <inheritdoc />
    ICaptureZone IScreenCapture.RegisterCaptureZone(CaptureRegion region, int downscaleLevel) => RegisterCaptureZone(region, downscaleLevel);

    /// <inheritdoc cref="IScreenCapture.RegisterCaptureZone" />
    public CaptureZone<TColor> RegisterCaptureZone(CaptureRegion region, int downscaleLevel = 0)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        lock (_captureZonesLock)
        {
            // Read once, so the zone gets the display it was validated against.
            Display display = Display;
            ValidateCaptureZoneAndThrow(region, downscaleLevel, display);

            (int width, int height, downscaleLevel) = CalculateScaledSize(region.Width, region.Height, downscaleLevel);

            CaptureZone<TColor> captureZone = new(display, region, width, height, downscaleLevel);
            Volatile.Write(ref _captureZones, [.. _captureZones, captureZone]);

            return captureZone;
        }
    }

    /// <summary>
    /// Validates the given values of a capture zone.
    /// </summary>
    /// <param name="region">The region of the zone on the display.</param>
    /// <param name="downscaleLevel">The downscale-level of the zone.</param>
    /// <param name="display">The display the zone is on.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="region"/> doesn't lie within the display or is empty, or <paramref name="downscaleLevel"/> is negative.
    /// </exception>
    private static void ValidateCaptureZoneAndThrow(CaptureRegion region, int downscaleLevel, Display display)
    {
        if (region.X < 0) throw new ArgumentOutOfRangeException(nameof(region), region, "X must not be negative.");
        if (region.Y < 0) throw new ArgumentOutOfRangeException(nameof(region), region, "Y must not be negative.");
        if (region.Width <= 0) throw new ArgumentOutOfRangeException(nameof(region), region, "Width must be greater than 0.");
        if (region.Height <= 0) throw new ArgumentOutOfRangeException(nameof(region), region, "Height must be greater than 0.");

        // Compared this way to not overflow.
        if (region.Width > (display.Width - region.X))
            throw new ArgumentOutOfRangeException(nameof(region), region, $"X + Width must not be greater than the display width ({display.Width}).");
        if (region.Height > (display.Height - region.Y))
            throw new ArgumentOutOfRangeException(nameof(region), region, $"Y + Height must not be greater than the display height ({display.Height}).");

        ArgumentOutOfRangeException.ThrowIfNegative(downscaleLevel);
    }

    /// <summary>
    /// Calculates the actual size when downscaling is used.
    /// </summary>
    /// <param name="width">The original width.</param>
    /// <param name="height">The original height.</param>
    /// <param name="downscaleLevel">The level of downscaling to be used.</param>
    /// <returns>The scaled width and height, and the downscale-level used (smaller than requested if the image is too small).</returns>
    private static (int width, int height, int downscaleLevel) CalculateScaledSize(int width, int height, int downscaleLevel)
    {
        if (downscaleLevel > 0)
            for (int i = 0; i < downscaleLevel; i++)
            {
                if ((width <= 1) && (height <= 1))
                {
                    downscaleLevel = i;
                    break;
                }

                width /= 2;
                height /= 2;
            }

        if (width < 1) width = 1;
        if (height < 1) height = 1;

        return (width, height, downscaleLevel);
    }

    /// <inheritdoc />
    bool IScreenCapture.UnregisterCaptureZone(ICaptureZone captureZone) => UnregisterCaptureZone(AsCaptureZone(captureZone));

    /// <inheritdoc cref="IScreenCapture.UnregisterCaptureZone" />
    /// <remarks>
    /// Overrides release the backend's resources of the zone after the base returned <c>true</c>. <see cref="PerformCaptureZoneUpdate"/> isn't called for it anymore.
    /// </remarks>
    public virtual bool UnregisterCaptureZone(CaptureZone<TColor> captureZone)
    {
        ArgumentNullException.ThrowIfNull(captureZone);
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        // Zone lock first: waits for a capture writing the zone.
        using (captureZone.EnterUpdateScope())
            lock (_captureZonesLock)
            {
                int index = Array.IndexOf(_captureZones, captureZone);
                if (index < 0) return false;

                CaptureZone<TColor>[] captureZones = new CaptureZone<TColor>[_captureZones.Length - 1];
                _captureZones.AsSpan(0, index).CopyTo(captureZones);
                _captureZones.AsSpan(index + 1).CopyTo(captureZones.AsSpan(index));
                Volatile.Write(ref _captureZones, captureZones);

                captureZone.SetUnregistered();

                return true;
            }
    }

    /// <inheritdoc />
    void IScreenCapture.UpdateCaptureZone(ICaptureZone captureZone, CaptureRegion? region, int? downscaleLevel)
        => UpdateCaptureZone(AsCaptureZone(captureZone), region, downscaleLevel);

    private static CaptureZone<TColor> AsCaptureZone(ICaptureZone captureZone)
    {
        ArgumentNullException.ThrowIfNull(captureZone);

        return captureZone as CaptureZone<TColor> ?? throw new ArgumentException(NOT_REGISTERED_MESSAGE, nameof(captureZone));
    }

    /// <inheritdoc cref="IScreenCapture.UpdateCaptureZone" />
    public void UpdateCaptureZone(CaptureZone<TColor> captureZone, CaptureRegion? region = null, int? downscaleLevel = null)
    {
        ArgumentNullException.ThrowIfNull(captureZone);
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        // Zone lock first: the zone doesn't change while it's written. The buffers are resized by the next capture.
        using (captureZone.EnterUpdateScope())
            lock (_captureZonesLock)
            {
                if (Array.IndexOf(_captureZones, captureZone) < 0)
                    throw new ArgumentException(NOT_REGISTERED_MESSAGE, nameof(captureZone));

                // Read once, like in RegisterCaptureZone.
                Display display = Display;
                captureZone.SetDisplay(display);

                CaptureRegion newRegion = region ?? captureZone.Region;
                int newDownscaleLevel = downscaleLevel ?? captureZone.DownscaleLevel;

                ValidateCaptureZoneAndThrow(newRegion, newDownscaleLevel, display);

                (int newWidth, int newHeight, newDownscaleLevel) = CalculateScaledSize(newRegion.Width, newRegion.Height, newDownscaleLevel);
                captureZone.Update(newRegion, newWidth, newHeight, newDownscaleLevel);
            }
    }

    /// <inheritdoc />
    public void Restart()
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        TryRestart();
        ApplyDisplayChange();
    }

    /// <summary>
    /// Restarts the capture, unless the backoff of a failed restart is still running.
    /// </summary>
    /// <remarks>
    /// Call it from <see cref="PerformScreenCapture"/> when the capture isn't running or lost its source, and return its result.
    /// </remarks>
    /// <returns><see cref="CaptureResult.Restarting"/> if the capture runs again; otherwise <see cref="CaptureResult.Unavailable"/>.</returns>
    protected CaptureResult RestartCapture()
    {
        if (Volatile.Read(ref _isDisposed)) return CaptureResult.Unavailable;

        lock (_restartLock)
            if (_restartBackoff.GetRemaining(Stopwatch.GetTimestamp()) > TimeSpan.Zero)
                return CaptureResult.Unavailable;

        return TryRestart() ? CaptureResult.Restarting : CaptureResult.Unavailable;
    }

    private bool TryRestart()
    {
        lock (_frameLock)
            _hasFrame = false;

        bool running;
        try
        {
            running = PerformRestart();
        }
        catch (Exception ex)
        {
            ReportError(ex);
            running = false;
        }

        lock (_restartLock)
        {
            if (running)
                _restartBackoff.Succeeded();
            else
                _restartBackoff.Failed(Stopwatch.GetTimestamp());
        }

        return running;
    }

    private void WaitForRestartAttempt()
    {
        while (!Volatile.Read(ref _isDisposed))
        {
            TimeSpan remaining;
            lock (_restartLock)
                remaining = _restartBackoff.GetRemaining(Stopwatch.GetTimestamp());

            if (remaining <= TimeSpan.Zero) return;

            Thread.Sleep(remaining < RESTART_WAIT_SLICE ? remaining : RESTART_WAIT_SLICE);
        }
    }

    /// <summary>
    /// (Re)starts the capture: releases its resources and creates them again.
    /// </summary>
    /// <remarks>
    /// Find the display by its <see cref="Display.Id"/>, read it again and pass it to <see cref="SetDisplay"/>, so changes are picked up.
    /// Report errors with <see cref="ReportError"/> and return <c>false</c> instead of throwing. After a failure the next attempt waits 100 ms, doubling up to 2 s.
    /// <para>
    /// Can run concurrently with <see cref="PerformScreenCapture"/>. Return <c>false</c> once the capture is disposed:
    /// check <see cref="IsDisposed"/> under the lock <see cref="Dispose(bool)"/> releases the resources under.
    /// </para>
    /// </remarks>
    /// <returns><c>true</c> if the capture runs; otherwise <c>false</c>.</returns>
    protected abstract bool PerformRestart();

    /// <inheritdoc />
    /// <remarks>
    /// A concurrent second call waits until the first one is done.
    /// </remarks>
    [SuppressMessage("Design", "CA1063:Implement IDisposable Correctly", Justification = "Dispose(bool) runs only once, after the flag is set.")]
    public void Dispose()
    {
        lock (_disposeLock)
        {
            if (_isDisposed) return;

            // Set first, so a racing restart sees it under the backend's lock.
            Volatile.Write(ref _isDisposed, true);
            Dispose(true);
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Releases the resources of this screen capture.
    /// </summary>
    /// <remarks>
    /// Called once, with <see cref="IsDisposed"/> already <c>true</c>. Overrides call the base and never throw.
    /// Release the resources under the lock <see cref="PerformRestart"/> uses.
    /// </remarks>
    /// <param name="disposing">Always <c>true</c>, screen captures have no finalizer.</param>
    protected virtual void Dispose(bool disposing) { }

    #endregion

    private sealed class DisplayBox(Display display)
    {
        public Display Display { get; } = display;
    }
}
