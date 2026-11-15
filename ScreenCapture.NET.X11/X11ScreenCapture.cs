using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using HPPH;

namespace ScreenCapture.NET;

/// <summary>
/// Represents a ScreenCapture using libX11.
/// https://x.org/releases/current/doc/libX11/libX11/libX11.html#XGetImage
/// </summary>
// ReSharper disable once InconsistentNaming
public sealed class X11ScreenCapture : AbstractScreenCapture<ColorBGRA>
{
    #region Properties & Fields

    private readonly Lock _captureLock = new();

    // Only used under _captureLock.
    private X11DisplayHandle? _display;
    private CULong _drawable;

    // Replaced under _captureLock, but read by zone updates without it (they hold a reference on the handle instead).
    private X11ImageHandle? _image;

    private bool _isDisposed;

    #endregion

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="X11ScreenCapture"/> class.
    /// </summary>
    /// <param name="display">The <see cref="Display"/> to duplicate.</param>
    internal X11ScreenCapture(Display display)
        : base(display)
    {
        Restart();
    }

    #endregion

    #region Methods

    /// <inheritdoc />
    protected override bool PerformScreenCapture()
    {
        lock (_captureLock)
        {
            if ((_display == null) || (_image == null))
            {
                Restart();
                return false;
            }

            X11.XGetSubImage(_display, _drawable, 0, 0, (uint)_image.Width, (uint)_image.Height, X11.ALL_PLANES, X11.ZPIXMAP, _image, 0, 0);

            return true;
        }
    }

    /// <inheritdoc />
    protected override void PerformCaptureZoneUpdate(CaptureZone<ColorBGRA> captureZone, Span<byte> buffer)
    {
        ArgumentNullException.ThrowIfNull(captureZone);

        X11ImageHandle? image = Volatile.Read(ref _image);
        if (image == null) return;

        // A concurrent Restart or Dispose closes the handle, but the memory is only freed once this reference is released.
        bool addedRef = false;
        try
        {
            image.DangerousAddRef(ref addedRef);

            RefImage<ColorBGRA> source = RefImage<ColorBGRA>.Wrap(image.Data, image.Width, image.Height, image.Stride);
            if (captureZone.DownscaleLevel == 0)
                CopyZone(source, captureZone, buffer);
            else
                DownscaleZone(source, captureZone, buffer);
        }
        finally
        {
            if (addedRef)
                image.DangerousRelease();
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void CopyZone(RefImage<ColorBGRA> image, CaptureZone<ColorBGRA> captureZone, Span<byte> buffer)
    {
        image[captureZone.X, captureZone.Y, captureZone.Width, captureZone.Height]
            .CopyTo(MemoryMarshal.Cast<byte, ColorBGRA>(buffer));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void DownscaleZone(RefImage<ColorBGRA> image, CaptureZone<ColorBGRA> captureZone, Span<byte> buffer)
    {
        RefImage<ColorBGRA> source = image[captureZone.X, captureZone.Y, captureZone.UnscaledWidth, captureZone.UnscaledHeight];
        Span<ColorBGRA> target = MemoryMarshal.Cast<byte, ColorBGRA>(buffer);

        int blockSize = 1 << captureZone.DownscaleLevel;

        int width = captureZone.Width;
        int height = captureZone.Height;

        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                target[(y * width) + x] = source[x * blockSize, y * blockSize, blockSize, blockSize].Average();
    }

    /// <inheritdoc />
    public override void Restart()
    {
        base.Restart();

        lock (_captureLock)
        {
            DisposeHandles();

            // On failure the capture stays without handles: CaptureScreen returns false and restarts on the next call.
            X11DisplayHandle? display = null;
            X11ImageHandle? image = null;
            try
            {
                display = X11.XOpenDisplay(X11.DISPLAY_NAME);
                if (display.IsInvalid) return;

                // XScreenOfDisplay doesn't check the index.
                if ((Display.Index < 0) || (Display.Index >= X11.XScreenCount(display))) return;

                nint screen = X11.XScreenOfDisplay(display, Display.Index);
                CULong drawable = X11.XRootWindowOfScreen(screen);

                image = X11.XGetImage(display, drawable, 0, 0, (uint)Display.Width, (uint)Display.Height, X11.ALL_PLANES, X11.ZPIXMAP);
                if (image.IsInvalid) return;

                image.ReadDescription();
                if (!image.IsBGRA) throw new NotSupportedException($"The X server uses an unsupported pixel format ({image.Format}). Only 32 bits per pixel with 8-bit BGR channels is supported.");

                _display = display;
                _drawable = drawable;
                Volatile.Write(ref _image, image);
                display = null;
                image = null;
            }
            catch { /**/ }
            finally
            {
                image?.Dispose();
                display?.Dispose();
            }
        }
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (_isDisposed) return;
        _isDisposed = true;

        base.Dispose(disposing);

        lock (_captureLock)
            DisposeHandles();
    }

    private void DisposeHandles()
    {
        X11ImageHandle? image = Volatile.Read(ref _image);
        Volatile.Write(ref _image, null);
        image?.Dispose();

        _display?.Dispose();
        _display = null;
        _drawable = default;
    }

    #endregion
}