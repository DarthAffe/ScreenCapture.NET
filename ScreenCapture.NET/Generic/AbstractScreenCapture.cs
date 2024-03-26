using System;
using System.Collections.Generic;
using System.Linq;

namespace ScreenCapture.NET;

/// <inheritdoc />
public abstract class AbstractScreenCapture<TColor> : IScreenCapture
    where TColor : struct, IColor
{
    #region Properties & Fields

    private bool _isDisposed;

    /// <summary>
    /// Gets a list of <see cref="CaptureZone{TColol}"/> registered on this ScreenCapture.
    /// </summary>
    protected HashSet<CaptureZone<TColor>> CaptureZones { get; } = new();

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

    ~AbstractScreenCapture() => Dispose(false);

    #endregion

    #region Methods

    /// <inheritdoc />
    public virtual bool CaptureScreen()
    {
        if (_isDisposed) throw new ObjectDisposedException(GetType().FullName);

        bool result;

        try
        {
            result = PerformScreenCapture();
        }
        catch
        {
            result = false;
        }

        lock (CaptureZones)
            foreach (CaptureZone<TColor> captureZone in CaptureZones.Where(x => x.AutoUpdate || x.IsUpdateRequested))
            {
                try
                {
                    PerformCaptureZoneUpdate(captureZone, captureZone.InternalBuffer);
                    captureZone.SetUpdated();
                }
                catch { /* */ }
            }

        OnUpdated(result);

        return result;
    }

    /// <summary>
    /// Performs the actual screen capture.
    /// </summary>
    /// <returns><c>true</c> if the screen was captured sucessfully; otherwise, <c>false</c>.</returns>
    protected abstract bool PerformScreenCapture();

    /// <summary>
    /// Performs an update of the given capture zone.
    /// </summary>
    /// <param name="captureZone">The capture zone to update.</param>
    /// <param name="buffer">The buffer containing the current pixel-data of the capture zone.</param>
    protected abstract void PerformCaptureZoneUpdate(CaptureZone<TColor> captureZone, in Span<byte> buffer);

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
        if (_isDisposed) throw new ObjectDisposedException(GetType().FullName);

        lock (CaptureZones)
        {
            ValidateCaptureZoneAndThrow(x, y, width, height, downscaleLevel);

            int unscaledWidth = width;
            int unscaledHeight = height;
            (width, height, downscaleLevel) = CalculateScaledSize(unscaledWidth, unscaledHeight, downscaleLevel);

            CaptureZone<TColor> captureZone = new(Display, x, y, width, height, downscaleLevel, unscaledWidth, unscaledHeight);
            CaptureZones.Add(captureZone);

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
        if (_isDisposed) throw new ObjectDisposedException(GetType().FullName);

        return CaptureZones.Remove(captureZone);
    }

    /// <inheritdoc />
    void IScreenCapture.UpdateCaptureZone(ICaptureZone captureZone, int? x, int? y, int? width, int? height, int? downscaleLevel)
        => UpdateCaptureZone(captureZone as CaptureZone<TColor> ?? throw new ArgumentException("Invalid capture-zone."), x, y, width, height, downscaleLevel);

    /// <inheritdoc cref="IScreenCapture.UpdateCaptureZone" />
    public virtual void UpdateCaptureZone(CaptureZone<TColor> captureZone, int? x = null, int? y = null, int? width = null, int? height = null, int? downscaleLevel = null)
    {
        if (_isDisposed) throw new ObjectDisposedException(GetType().FullName);

        lock (CaptureZones)
        {
            if (!CaptureZones.Contains(captureZone))
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
        if (_isDisposed) throw new ObjectDisposedException(GetType().FullName);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_isDisposed) return;

        try
        {
            Dispose(true);
        }
        catch { /* don't throw in dispose! */ }

        GC.SuppressFinalize(this);

        _isDisposed = true;
    }

    /// <inheritdoc cref="IDisposable.Dispose" />
    protected virtual void Dispose(bool disposing) { }

    #endregion
}
