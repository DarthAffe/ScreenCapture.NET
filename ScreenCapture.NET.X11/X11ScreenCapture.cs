using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
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

    private readonly object _captureLock = new();

    private nint _display;
    private nint _drawable;
    private nint _imageHandle;
    private X11.XImage _image;
    private int _size;
    private unsafe ReadOnlySpan<byte> Data => new(_image.data, _size);

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
            if ((_display == 0) || (_imageHandle == 0))
            {
                Restart();
                return false;
            }

            X11.XGetSubImage(_display, _drawable, 0, 0, (uint)Display.Width, (uint)Display.Height, X11.ALL_PLANES, X11.ZPIXMAP, _imageHandle, 0, 0);

            return true;
        }
    }

    /// <inheritdoc />
    protected override void PerformCaptureZoneUpdate(CaptureZone<ColorBGRA> captureZone, Span<byte> buffer)
    {
        using IDisposable @lock = captureZone.Lock();
        {
            if (captureZone.DownscaleLevel == 0)
                CopyZone(captureZone, buffer);
            else
                DownscaleZone(captureZone, buffer);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void CopyZone(CaptureZone<ColorBGRA> captureZone, Span<byte> buffer)
    {
        RefImage<ColorBGRA>.Wrap(Data, Display.Width, Display.Height, _image.bytes_per_line)[captureZone.X, captureZone.Y, captureZone.Width, captureZone.Height]
                           .CopyTo(MemoryMarshal.Cast<byte, ColorBGRA>(buffer));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void DownscaleZone(CaptureZone<ColorBGRA> captureZone, Span<byte> buffer)
    {
        RefImage<ColorBGRA> source = RefImage<ColorBGRA>.Wrap(Data, Display.Width, Display.Height, _image.bytes_per_line)[captureZone.X, captureZone.Y, captureZone.UnscaledWidth, captureZone.UnscaledHeight];
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
            DisposeDisplay();
            try
            {
                _display = X11.XOpenDisplay(X11.DISPLAY_NAME);

                nint screen = X11.XScreenOfDisplay(_display, Display.Index);
                _drawable = X11.XRootWindowOfScreen(screen);
                _imageHandle = X11.XGetImage(_display, _drawable, 0, 0, (uint)Display.Width, (uint)Display.Height, X11.ALL_PLANES, X11.ZPIXMAP);
                _image = Marshal.PtrToStructure<X11.XImage>(_imageHandle);
                _size = _image.bytes_per_line * _image.height;

                if (_image.bits_per_pixel != (ColorBGRA.ColorFormat.BytesPerPixel * 8)) throw new NotSupportedException("The X-Server is configured to a not supported pixel-format. Needs to be 32 bit per pixel BGR.");
            }
            catch
            {
                DisposeDisplay();
            }
        }
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        lock (_captureLock)
        {
            try { DisposeDisplay(); }
            catch { /**/ }
        }
    }

    private void DisposeDisplay()
    {
        if (_imageHandle != 0)
            try { X11.XDestroyImage(_imageHandle); } catch { /**/ }

        _image = default;

        if (_display != 0)
            try { X11.XCloseDisplay(_display); } catch { /**/ }
    }

    #endregion
}