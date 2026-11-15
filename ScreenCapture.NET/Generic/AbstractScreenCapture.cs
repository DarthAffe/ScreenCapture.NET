using System;
using System.Buffers;
using System.Collections.Generic;
using System.Threading;
using HPPH;

namespace ScreenCapture.NET;

/// <inheritdoc />
/// <remarks>
/// Lock order: a zone lock (<see cref="CaptureZone{TColor}.Lock"/>) is always acquired before any lock of the screen capture,
/// and the library never acquires a zone lock while holding one of its own locks.
/// It's therefore safe to call any method of the screen capture while holding a zone lock.
/// </remarks>
public abstract class AbstractScreenCapture<TColor> : IScreenCapture
    where TColor : unmanaged, IColor<TColor>
{
    #region Constants

    // Zone counts up to this use a stack buffer for the per-capture "updated" flags; more fall back to the ArrayPool.
    private const int UPDATED_STACKALLOC_THRESHOLD = 32;

    #endregion

    #region Properties & Fields

    private bool _isDisposed;

    // Guards all changes to the zone set and to zone positions/sizes. Acquired after a zone lock, never before.
    private readonly Lock _captureZonesLock = new();

    // Copy-on-write: replaced (never modified) under _captureZonesLock, so a capture can walk it without a lock.
    private CaptureZone<TColor>[] _captureZones = [];

    /// <summary>
    /// Gets a snapshot of the <see cref="CaptureZone{TColor}"/> registered on this ScreenCapture.
    /// </summary>
    /// <remarks>
    /// The snapshot doesn't change when zones are registered or unregistered later.
    /// </remarks>
    // ReSharper disable once InconsistentlySynchronizedField
    protected IReadOnlyList<CaptureZone<TColor>> CaptureZones => Volatile.Read(ref _captureZones);

    /// <inheritdoc />
    public Display Display { get; }

    #endregion

    #region Events

    /// <inheritdoc />
    public event EventHandler<ScreenCaptureUpdatedEventArgs>? Updated;

    #endregion

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="AbstractScreenCapture{T}"/> class.
    /// </summary>
    /// <param name="display">The <see cref="Display"/> to duplicate.</param>
    protected AbstractScreenCapture(Display display)
    {
        this.Display = display;
    }

    #endregion

    #region Methods

    /// <inheritdoc />
    public virtual bool CaptureScreen()
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        bool result;

        try
        {
            result = PerformScreenCapture();
        }
        catch
        {
            result = false;
        }

        // ReSharper disable once InconsistentlySynchronizedField
        CaptureZone<TColor>[] captureZones = Volatile.Read(ref _captureZones);
        if (captureZones.Length > 0)
        {
            // Every slot is written before it's read. If an update throws, the rented array is simply not returned, which the pool tolerates.
            bool[]? rented = null;
            Span<bool> updated = captureZones.Length <= UPDATED_STACKALLOC_THRESHOLD
                                     ? stackalloc bool[UPDATED_STACKALLOC_THRESHOLD]
                                     : (rented = ArrayPool<bool>.Shared.Rent(captureZones.Length));
            
            for (int i = 0; i < captureZones.Length; i++)
                updated[i] = TryUpdateCaptureZone(captureZones[i]);

            // Raised after the loop and without holding any lock, so handlers can (un)register or update zones and lock other zones.
            for (int i = 0; i < captureZones.Length; i++)
                if (updated[i])
                    captureZones[i].RaiseUpdated();

            if (rented != null)
                ArrayPool<bool>.Shared.Return(rented);
        }

        OnUpdated(result);

        return result;
    }

    private bool TryUpdateCaptureZone(CaptureZone<TColor> captureZone)
    {
        if (!captureZone.AutoUpdate && !captureZone.IsUpdateRequested) return false;

        try
        {
            using (captureZone.Lock())
                PerformCaptureZoneUpdate(captureZone, captureZone.InternalBuffer);

            captureZone.ResetUpdateRequest();

            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Performs the actual screen capture.
    /// </summary>
    /// <returns><c>true</c> if the screen was captured successfully; otherwise, <c>false</c>.</returns>
    protected abstract bool PerformScreenCapture();

    /// <summary>
    /// Performs an update of the given capture zone.
    /// </summary>
    /// <remarks>
    /// Called with the lock of <paramref name="captureZone"/> held, so its position, size and buffer don't change during the update.
    /// The zone might have been unregistered since the capture started.
    /// </remarks>
    /// <param name="captureZone">The capture zone to update.</param>
    /// <param name="buffer">The buffer containing the current pixel-data of the capture zone.</param>
    protected abstract void PerformCaptureZoneUpdate(CaptureZone<TColor> captureZone, Span<byte> buffer);

    /// <summary>
    /// Raises the <see cref="Updated"/>-event.
    /// </summary>
    /// <param name="result">A bool indicating whether the update was successful or not.</param>
    protected virtual void OnUpdated(bool result)
    {
        try
        {
            Updated?.Invoke(this, new ScreenCaptureUpdatedEventArgs(result));
        }
        catch { /**/ }
    }

    /// <inheritdoc />
    ICaptureZone IScreenCapture.RegisterCaptureZone(int x, int y, int width, int height, int downscaleLevel) => RegisterCaptureZone(x, y, width, height, downscaleLevel);

    /// <inheritdoc cref="IScreenCapture.RegisterCaptureZone" />
    public virtual CaptureZone<TColor> RegisterCaptureZone(int x, int y, int width, int height, int downscaleLevel = 0)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        lock (_captureZonesLock)
        {
            ValidateCaptureZoneAndThrow(x, y, width, height, downscaleLevel);

            int unscaledWidth = width;
            int unscaledHeight = height;
            (width, height, downscaleLevel) = CalculateScaledSize(unscaledWidth, unscaledHeight, downscaleLevel);

            CaptureZone<TColor> captureZone = new(Display, x, y, width, height, downscaleLevel, unscaledWidth, unscaledHeight);
            Volatile.Write(ref _captureZones, [.. _captureZones, captureZone]);

            return captureZone;
        }
    }

    /// <summary>
    /// Validates the given values of a capture zone.
    /// </summary>
    /// <param name="x">The X-location of the zone.</param>
    /// <param name="y">The Y-location of the zone.</param>
    /// <param name="width">The width of the zone.</param>
    /// <param name="height">The height of the zone.</param>
    /// <param name="downscaleLevel">The downscale-level of the zone.</param>
    /// <exception cref="ArgumentException">Throws if some of the provided data is not valid.</exception>
    protected virtual void ValidateCaptureZoneAndThrow(int x, int y, int width, int height, int downscaleLevel)
    {
        if (x < 0) throw new ArgumentException("x < 0");
        if (y < 0) throw new ArgumentException("y < 0");
        if (width <= 0) throw new ArgumentException("with <= 0");
        if (height <= 0) throw new ArgumentException("height <= 0");
        if ((x + width) > Display.Width) throw new ArgumentException("x + width > Display width");
        if ((y + height) > Display.Height) throw new ArgumentException("y + height > Display height");
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
    bool IScreenCapture.UnregisterCaptureZone(ICaptureZone captureZone) => UnregisterCaptureZone(captureZone as CaptureZone<TColor> ?? throw new ArgumentException("Invalid capture-zone."));

    /// <inheritdoc cref="IScreenCapture.UnregisterCaptureZone" />
    public virtual bool UnregisterCaptureZone(CaptureZone<TColor> captureZone)
    {
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
    void IScreenCapture.UpdateCaptureZone(ICaptureZone captureZone, int? x, int? y, int? width, int? height, int? downscaleLevel)
        => UpdateCaptureZone(captureZone as CaptureZone<TColor> ?? throw new ArgumentException("Invalid capture-zone."), x, y, width, height, downscaleLevel);

    /// <inheritdoc cref="IScreenCapture.UpdateCaptureZone" />
    public virtual void UpdateCaptureZone(CaptureZone<TColor> captureZone, int? x = null, int? y = null, int? width = null, int? height = null, int? downscaleLevel = null)
    {
        ArgumentNullException.ThrowIfNull(captureZone);
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        // Zone lock first (see the lock order in the class remarks): the zone doesn't change while a capture updates it.
        using (captureZone.Lock())
        lock (_captureZonesLock)
        {
            if (Array.IndexOf(_captureZones, captureZone) < 0)
                throw new ArgumentException("The capture zone is not registered to this ScreenCapture", nameof(captureZone));

            int newX = x ?? captureZone.X;
            int newY = y ?? captureZone.Y;
            int newUnscaledWidth = width ?? captureZone.UnscaledWidth;
            int newUnscaledHeight = height ?? captureZone.UnscaledHeight;
            int newDownscaleLevel = downscaleLevel ?? captureZone.DownscaleLevel;

            ValidateCaptureZoneAndThrow(newX, newY, newUnscaledWidth, newUnscaledHeight, newDownscaleLevel);

            captureZone.X = newX;
            captureZone.Y = newY;

            if ((width != null) || (height != null) || (downscaleLevel != null))
            {
                (int newWidth, int newHeight, newDownscaleLevel) = CalculateScaledSize(newUnscaledWidth, newUnscaledHeight, newDownscaleLevel);
                captureZone.Resize(newWidth, newHeight, newDownscaleLevel, newUnscaledWidth, newUnscaledHeight);
            }
        }
    }

    /// <inheritdoc />
    public virtual void Restart()
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
    }

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
}
