using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using HPPH;

namespace ScreenCapture.NET;

/// <inheritdoc />
/// <remarks>
/// Lock order: a zone lock (held while a zone is written or changed, and by frames of single-buffered zones, see <see cref="CaptureZone{TColor}.Lock"/>)
/// is always acquired before any lock of the screen capture, and the library never acquires a zone lock while holding one of its own locks.
/// It's therefore safe to call any method of the screen capture while holding a frame.
/// </remarks>
public abstract class AbstractScreenCapture<TColor> : IScreenCapture
    where TColor : unmanaged, IColor<TColor>
{
    #region Constants

    // Zone counts up to this use a stack buffer for the per-capture "updated" flags; more fall back to the ArrayPool.
    private const int UPDATED_STACKALLOC_THRESHOLD = 32;

    private const string NOT_REGISTERED_MESSAGE = "The capture zone is not registered to this screen capture.";

    // Errors reported without a CaptureScreen call raising them (e.g. by repeated Restart calls) are dropped beyond this, oldest first.
    private const int MAX_PENDING_ERRORS = 16;

    private static readonly TimeSpan RESTART_WAIT_SLICE = TimeSpan.FromMilliseconds(50);

    #endregion

    #region Properties & Fields

    private bool _isDisposed;

    /// <inheritdoc />
    /// <remarks>
    /// Set at the start of <see cref="Dispose()"/>, before the backend released its resources.
    /// </remarks>
    public bool IsDisposed => Volatile.Read(ref _isDisposed);

    private Exception? _lastError;

    // Guards _pendingErrors. A leaf lock, so errors can be reported while holding any other lock.
    private readonly Lock _errorLock = new();
    private readonly Queue<Exception> _pendingErrors = new();

    // Guards _frameNumber, _frameTimestamp and _hasFrame, which always describe the same frame. A leaf lock.
    private readonly Lock _frameLock = new();
    private long _frameNumber;
    private long _frameTimestamp;

    private bool _hasFrame;

    // Guards the restart backoff. A leaf lock: never held while restarting, so it doesn't matter which backend locks the caller holds.
    private readonly Lock _restartLock = new();
    private readonly Backoff _restartBackoff = new();

    // Guards all changes to the zone set and to zone positions/sizes. Acquired after a zone lock, never before.
    private readonly Lock _captureZonesLock = new();

    // Copy-on-write: replaced (never modified) under _captureZonesLock, so a capture can walk it without a lock.
    private CaptureZone<TColor>[] _captureZones = [];

    // Guards the display changes (_display is replaced under it, but read without it), _displayVersion, _appliedDisplayVersion and _pendingDisplayChange.
    // A leaf lock, so backends can set the display while holding their locks.
    private readonly Lock _displayLock = new();

    // Boxed, so the display (a large struct) is replaced atomically and read without a lock.
    private DisplayBox _display;

    // Counts the display changes; the zones follow up to _appliedDisplayVersion.
    private int _displayVersion;
    private int _appliedDisplayVersion;

    private ScreenCaptureDisplayChangedEventArgs? _pendingDisplayChange;

    /// <summary>
    /// Gets a snapshot of the <see cref="CaptureZone{TColor}"/> registered on this ScreenCapture.
    /// </summary>
    /// <remarks>
    /// The snapshot doesn't change when zones are registered or unregistered later.
    /// </remarks>
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
    public virtual CaptureResult CaptureScreen()
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
            // Every slot is written before it's read. If an update throws, the rented array is simply not returned, which the pool tolerates.
            bool[]? rented = null;
            Span<bool> updated = captureZones.Length <= UPDATED_STACKALLOC_THRESHOLD
                                     ? stackalloc bool[UPDATED_STACKALLOC_THRESHOLD]
                                     : (rented = ArrayPool<bool>.Shared.Rent(captureZones.Length));

            for (int i = 0; i < captureZones.Length; i++)
                updated[i] = TryUpdateCaptureZone(captureZones[i], frameNumber);

            // Raised after the loop and without holding any lock, so handlers can (un)register or update zones and lock other zones.
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

    // Raised here instead of where the errors occur: there the backends often hold their locks, and a handler that locks a zone
    // could deadlock against a thread that holds a frame of that zone and calls into the capture.
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
    /// Sets the display the capture duplicates now, e.g. after <see cref="PerformRestart"/> read it again and found it changed (resolution, rotation, position).
    /// </summary>
    /// <remarks>
    /// Call it once the resources of the backend match the new display, e.g. at the end of a successful restart. It can be called while holding any lock.
    /// The zones follow the new display before the next capture writes them (and before <see cref="Restart"/> returns): zones that don't fit anymore become invalid
    /// (see <see cref="ICaptureZone.IsValid"/>). <see cref="DisplayChanged"/> is raised by the next <see cref="CaptureScreen"/>.
    /// Nothing happens if the display didn't change.
    /// </remarks>
    /// <param name="display">The current display.</param>
    /// <exception cref="ArgumentException"><paramref name="display"/> has another <see cref="Display.Id"/>: a capture always duplicates the same display.</exception>
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
    /// Reports an error the capture recovers from (or reports as its result) instead of throwing it.
    /// </summary>
    /// <remarks>
    /// Sets <see cref="LastError"/> right away; <see cref="ErrorOccurred"/> is raised by the next <see cref="CaptureScreen"/>.
    /// It can be called while holding any lock. Exceptions thrown by <see cref="PerformScreenCapture"/> and <see cref="PerformCaptureZoneUpdate"/> are reported by the base class.
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
                if (!captureZone.IsValid) return false;

                ZoneBuffer? buffer = captureZone.BeginWrite();
                if (buffer == null) return false;

                bool written = false;
                try
                {
                    written = PerformCaptureZoneUpdate(captureZone, buffer.Data);
                }
                finally
                {
                    captureZone.EndWrite(buffer, written);
                }

                if (!written) return false;

                captureZone.SetWritten(frameNumber);
            }

            captureZone.ResetUpdateRequest();

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
    /// Exceptions are reported (see <see cref="ReportError"/>) and returned as <see cref="CaptureResult.Failed"/>.
    /// Errors the capture recovers from (e.g. by restarting) aren't thrown, but should be reported with <see cref="ReportError"/>.
    /// </remarks>
    /// <param name="timestamp">
    /// For <see cref="CaptureResult.NewFrame"/>: when the frame was shown, as a <see cref="Stopwatch"/> timestamp,
    /// or 0 to use the time this method returned. Ignored for other results.
    /// </param>
    /// <returns><see cref="CaptureResult.NewFrame"/> if a new frame was captured; otherwise why not.</returns>
    protected abstract CaptureResult PerformScreenCapture(out long timestamp);

    /// <summary>
    /// Performs an update of the given capture zone.
    /// </summary>
    /// <remarks>
    /// Called with the lock of <paramref name="captureZone"/> held, so its position and size don't change during the update.
    /// The zone might have been unregistered since the capture started.
    /// <para>
    /// Only called for valid zones (see <see cref="ICaptureZone.IsValid"/>), whose region lies within <see cref="Display"/>.
    /// But a <see cref="Restart"/> on another thread can change the display before the zones follow it:
    /// check that the region lies within the frame the backend holds (see <see cref="CaptureRegion.IsWithin"/>) and return <c>false</c> if it doesn't.
    /// </para>
    /// <para>
    /// Called after <see cref="CaptureResult.NewFrame"/>, and after <see cref="CaptureResult.NoChange"/> or <see cref="CaptureResult.Timeout"/>
    /// for zones that requested an update or don't show the last frame yet (e.g. new or changed zones): the backend has to keep the last frame for that.
    /// Not called after other results or after <see cref="Restart"/> until the next <see cref="CaptureResult.NewFrame"/>.
    /// </para>
    /// <para>
    /// <paramref name="buffer"/> has the zone's current size (<see cref="CaptureZone{TColor}.Stride"/> * <see cref="CaptureZone{TColor}.Height"/>).
    /// For a double-buffered zone it's the back buffer, which contains an older frame (or zeros) and is only shown if this returns <c>true</c>.
    /// </para>
    /// </remarks>
    /// <param name="captureZone">The capture zone to update.</param>
    /// <param name="buffer">The buffer to write the pixels of the capture zone to.</param>
    /// <returns><c>true</c> if <paramref name="buffer"/> was filled; <c>false</c> if there was nothing to write (e.g. no captured frame yet), so the zone keeps its pixels.</returns>
    protected abstract bool PerformCaptureZoneUpdate(CaptureZone<TColor> captureZone, Span<byte> buffer);

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
    ICaptureZone IScreenCapture.RegisterCaptureZone(CaptureRegion region, int downscaleLevel, bool doubleBuffered) => RegisterCaptureZone(region, downscaleLevel, doubleBuffered);

    /// <inheritdoc cref="IScreenCapture.RegisterCaptureZone" />
    public virtual CaptureZone<TColor> RegisterCaptureZone(CaptureRegion region, int downscaleLevel = 0, bool doubleBuffered = true)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        lock (_captureZonesLock)
        {
            ValidateCaptureZoneAndThrow(region, downscaleLevel);

            (int width, int height, downscaleLevel) = CalculateScaledSize(region.Width, region.Height, downscaleLevel);

            CaptureZone<TColor> captureZone = new(Display, region, width, height, downscaleLevel, doubleBuffered);
            Volatile.Write(ref _captureZones, [.. _captureZones, captureZone]);

            return captureZone;
        }
    }

    /// <summary>
    /// Validates the given values of a capture zone.
    /// </summary>
    /// <param name="region">The region of the zone on the display.</param>
    /// <param name="downscaleLevel">The downscale-level of the zone.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="region"/> doesn't lie within the display or is empty, or <paramref name="downscaleLevel"/> is negative.
    /// </exception>
    protected virtual void ValidateCaptureZoneAndThrow(CaptureRegion region, int downscaleLevel)
    {
        if (region.X < 0) throw new ArgumentOutOfRangeException(nameof(region), region, "X must not be negative.");
        if (region.Y < 0) throw new ArgumentOutOfRangeException(nameof(region), region, "Y must not be negative.");
        if (region.Width <= 0) throw new ArgumentOutOfRangeException(nameof(region), region, "Width must be greater than 0.");
        if (region.Height <= 0) throw new ArgumentOutOfRangeException(nameof(region), region, "Height must be greater than 0.");

        // Compared this way, X + Width can't overflow (X and Y aren't negative here).
        if (region.Width > (Display.Width - region.X))
            throw new ArgumentOutOfRangeException(nameof(region), region, $"X + Width must not be greater than the display width ({Display.Width}).");
        if (region.Height > (Display.Height - region.Y))
            throw new ArgumentOutOfRangeException(nameof(region), region, $"Y + Height must not be greater than the display height ({Display.Height}).");

        ArgumentOutOfRangeException.ThrowIfNegative(downscaleLevel);
    }

    /// <summary>
    /// Calculates the actual size when downscaling is used.
    /// </summary>
    /// <param name="width">The original width.</param>
    /// <param name="height">The original height.</param>
    /// <param name="downscaleLevel">The level of downscaling to be used.</param>
    /// <returns>A tuple containing the scaled width, the scaled height and the downscale-level used. (This can be smaller then the one provided if the image is not big enough to scale down that often.)</returns>
    protected virtual (int width, int height, int downscaleLevel) CalculateScaledSize(int width, int height, int downscaleLevel)
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
    public virtual bool UnregisterCaptureZone(CaptureZone<TColor> captureZone)
    {
        ArgumentNullException.ThrowIfNull(captureZone);
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        lock (_captureZonesLock)
        {
            int index = Array.IndexOf(_captureZones, captureZone);
            if (index < 0) return false;

            CaptureZone<TColor>[] captureZones = new CaptureZone<TColor>[_captureZones.Length - 1];
            _captureZones.AsSpan(0, index).CopyTo(captureZones);
            _captureZones.AsSpan(index + 1).CopyTo(captureZones.AsSpan(index));
            Volatile.Write(ref _captureZones, captureZones);

            return true;
        }
    }

    /// <inheritdoc />
    void IScreenCapture.UpdateCaptureZone(ICaptureZone captureZone, CaptureRegion? region, int? downscaleLevel)
        => UpdateCaptureZone(AsCaptureZone(captureZone), region, downscaleLevel);

    // A zone of another color format (or another ICaptureZone implementation) can't be registered to this capture.
    private static CaptureZone<TColor> AsCaptureZone(ICaptureZone captureZone)
    {
        ArgumentNullException.ThrowIfNull(captureZone);

        return captureZone as CaptureZone<TColor> ?? throw new ArgumentException(NOT_REGISTERED_MESSAGE, nameof(captureZone));
    }

    /// <inheritdoc cref="IScreenCapture.UpdateCaptureZone" />
    public virtual void UpdateCaptureZone(CaptureZone<TColor> captureZone, CaptureRegion? region = null, int? downscaleLevel = null)
    {
        ArgumentNullException.ThrowIfNull(captureZone);
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        // Zone lock first (see the lock order in the class remarks): the zone doesn't change while a capture writes it.
        // The buffers are resized by the next capture, frames keep their size.
        using (captureZone.EnterUpdateScope())
            lock (_captureZonesLock)
            {
                if (Array.IndexOf(_captureZones, captureZone) < 0)
                    throw new ArgumentException(NOT_REGISTERED_MESSAGE, nameof(captureZone));

                captureZone.SetDisplay(Display);

                CaptureRegion newRegion = region ?? captureZone.Region;
                int newDownscaleLevel = downscaleLevel ?? captureZone.DownscaleLevel;

                ValidateCaptureZoneAndThrow(newRegion, newDownscaleLevel);

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
    /// Restarts the capture if it's due: right away the first time, after a failed restart only once the backoff has passed.
    /// </summary>
    /// <remarks>
    /// Call this from <see cref="PerformScreenCapture"/> when the capture isn't running or lost its source, and return its result.
    /// <see cref="CaptureScreen"/> already waited for the backoff before calling <see cref="PerformScreenCapture"/>, so the restart is usually due.
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
    /// (Re)starts the capture: releases what the capture holds and creates it again.
    /// </summary>
    /// <remarks>
    /// Called by <see cref="Restart"/>, by <see cref="RestartCapture"/> and usually by the constructor of the backend (through <see cref="Restart"/>).
    /// Find the display by its <see cref="Display.Id"/> (an index can point to another display after displays were added or removed),
    /// read it again and pass it to <see cref="SetDisplay"/>, so a changed resolution, rotation or position is picked up.
    /// Don't throw: report errors with <see cref="ReportError"/> and return <c>false</c> (exceptions are reported and count as a failed restart).
    /// After a failed restart, <see cref="RestartCapture"/> waits 100 ms before the next attempt, doubling with every further failure up to 2 s.
    /// <para>
    /// It can run concurrently with <see cref="PerformScreenCapture"/> (from <see cref="Restart"/>) and must return <c>false</c> once the capture is disposed.
    /// </para>
    /// </remarks>
    /// <returns><c>true</c> if the capture runs; otherwise <c>false</c>.</returns>
    protected abstract bool PerformRestart();

    /// <inheritdoc />
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Releases the resources of this screen capture and marks it as disposed.
    /// </summary>
    /// <remarks>
    /// This can be called more than once. Overrides release their resources only on the first call, call the base implementation and never throw.
    /// </remarks>
    /// <param name="disposing">Always <c>true</c>: screen captures have no finalizer.</param>
    protected virtual void Dispose(bool disposing) => _isDisposed = true;

    #endregion

    private sealed class DisplayBox(Display display)
    {
        public Display Display { get; } = display;
    }
}
