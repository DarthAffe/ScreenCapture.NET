using System;
using System.Collections.Generic;
using System.Linq;

namespace ScreenCapture.NET;

public abstract class AbstractScreenCapture<TColor> : IScreenCapture
    where TColor : struct, IColor
{
    #region Properties & Fields

    private bool _isDisposed;
    private int _indexCounter = 0;

    protected HashSet<CaptureZone<TColor>> CaptureZones { get; } = new();

    public Display Display { get; }

    #endregion

    #region Events

    public event EventHandler<ScreenCaptureUpdatedEventArgs>? Updated;

    #endregion

    #region Constructors

    protected AbstractScreenCapture(Display display)
    {
        this.Display = display;
    }

    ~AbstractScreenCapture() => Dispose(false);

    #endregion

    #region Methods

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

    protected abstract bool PerformScreenCapture();

    protected abstract void PerformCaptureZoneUpdate(CaptureZone<TColor> captureZone, in Span<byte> buffer);

    protected virtual void OnUpdated(bool result)
    {
        try
        {
            Updated?.Invoke(this, new ScreenCaptureUpdatedEventArgs(result));
        }
        catch { /**/ }
    }

    ICaptureZone IScreenCapture.RegisterCaptureZone(int x, int y, int width, int height, int downscaleLevel) => RegisterCaptureZone(x, y, width, height, downscaleLevel);
    public virtual CaptureZone<TColor> RegisterCaptureZone(int x, int y, int width, int height, int downscaleLevel = 0)
    {
        if (_isDisposed) throw new ObjectDisposedException(GetType().FullName);

        lock (CaptureZones)
        {
            ValidateCaptureZoneAndThrow(x, y, width, height, downscaleLevel);

            int unscaledWidth = width;
            int unscaledHeight = height;
            (width, height, downscaleLevel) = CalculateScaledSize(unscaledWidth, unscaledHeight, downscaleLevel);

            CaptureZone<TColor> captureZone = new(_indexCounter++, Display, x, y, width, height, downscaleLevel, unscaledWidth, unscaledHeight);
            CaptureZones.Add(captureZone);

            return captureZone;
        }
    }

    protected virtual void ValidateCaptureZoneAndThrow(int x, int y, int width, int height, int downscaleLevel)
    {
        if (x < 0) throw new ArgumentException("x < 0");
        if (y < 0) throw new ArgumentException("y < 0");
        if (width <= 0) throw new ArgumentException("with <= 0");
        if (height <= 0) throw new ArgumentException("height <= 0");
        if ((x + width) > Display.Width) throw new ArgumentException("x + width > Display width");
        if ((y + height) > Display.Height) throw new ArgumentException("y + height > Display height");
    }

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

    bool IScreenCapture.UnregisterCaptureZone(ICaptureZone captureZone) => UnregisterCaptureZone(captureZone as CaptureZone<TColor> ?? throw new ArgumentException("Invalid capture-zone."));
    public virtual bool UnregisterCaptureZone(CaptureZone<TColor> captureZone)
    {
        if (_isDisposed) throw new ObjectDisposedException(GetType().FullName);

        return CaptureZones.Remove(captureZone);
    }

    void IScreenCapture.UpdateCaptureZone(ICaptureZone captureZone, int? x, int? y, int? width, int? height, int? downscaleLevel)
        => UpdateCaptureZone(captureZone as CaptureZone<TColor> ?? throw new ArgumentException("Invalid capture-zone."), x, y, width, height, downscaleLevel);
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

    protected virtual void Dispose(bool disposing) { }

    #endregion
}