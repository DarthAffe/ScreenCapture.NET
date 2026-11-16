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
/// <remarks>
/// The first connection to the X server installs a process-wide Xlib error handler (<c>XSetErrorHandler</c>) that stays installed.
/// It records errors of this library's connections (a failed capture restarts instead of Xlib terminating the process)
/// and passes errors of all other connections to the handler that was installed before.
/// If other code installs its own handler afterwards without passing errors on, X errors of the capture are handled by that code.
/// </remarks>
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
    protected override CaptureResult PerformScreenCapture(out long timestamp)
    {
        // X11 can't tell when the frame was presented: the base uses the time the capture returned.
        timestamp = 0;

        lock (_captureLock)
        {
            if ((_display == null) || (_image == null))
                return RestartCapture();

            _display.ClearError();
            nint result = X11.XGetSubImage(_display, _drawable, 0, 0, (uint)_image.Width, (uint)_image.Height, X11.ALL_PLANES, X11.ZPIXMAP, _image, 0, 0);

            // E.g. BadMatch if the screen got smaller than the image. Starting over picks up the current state.
            if ((result == 0) || (_display.LastError != null))
            {
                ReportError(new InvalidOperationException($"XGetSubImage failed{FormatXError(_display.LastError)}."));
                return RestartCapture();
            }

            // XGetSubImage has no change detection: every successful read counts as a new frame.
            return CaptureResult.NewFrame;
        }
    }

    private static string FormatXError(X11Error? error) => error == null ? string.Empty : $" with {error}";

    /// <inheritdoc />
    protected override bool PerformCaptureZoneUpdate(CaptureZone<ColorBGRA> captureZone, Span<byte> buffer)
    {
        ArgumentNullException.ThrowIfNull(captureZone);

        X11ImageHandle? image = Volatile.Read(ref _image);
        if (image == null) return false;

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

            return true;
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
        image[captureZone.Region.X, captureZone.Region.Y, captureZone.Width, captureZone.Height]
            .CopyTo(MemoryMarshal.Cast<byte, ColorBGRA>(buffer));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void DownscaleZone(RefImage<ColorBGRA> image, CaptureZone<ColorBGRA> captureZone, Span<byte> buffer)
    {
        RefImage<ColorBGRA> source = image[captureZone.Region.X, captureZone.Region.Y, captureZone.Region.Width, captureZone.Region.Height];
        Span<ColorBGRA> target = MemoryMarshal.Cast<byte, ColorBGRA>(buffer);

        int blockSize = 1 << captureZone.DownscaleLevel;

        int width = captureZone.Width;
        int height = captureZone.Height;

        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                target[(y * width) + x] = source[x * blockSize, y * blockSize, blockSize, blockSize].Average();
    }

    /// <inheritdoc />
    protected override bool PerformRestart()
    {
        lock (_captureLock)
        {
            // Dispose releases everything under this lock after setting the flag: nothing created here may outlive it.
            if (_isDisposed) return false;

            DisposeHandles();

            // On failure the capture stays without handles: CaptureScreen returns Unavailable and restarts with the next attempt.
            X11DisplayHandle? display = null;
            X11ImageHandle? image = null;
            try
            {
                display = X11DisplayHandle.Open();
                if (display.IsInvalid) throw X11DisplayHandle.CreateOpenFailedException();

                // XScreenOfDisplay doesn't check the index.
                int screenCount = X11.XScreenCount(display);
                if ((Display.Index < 0) || (Display.Index >= screenCount))
                    throw new InvalidOperationException($"The X server has no screen {Display.Index} (it has {screenCount}).");

                nint screen = X11.XScreenOfDisplay(display, Display.Index);
                CULong drawable = X11.XRootWindowOfScreen(screen);

                image = X11.XGetImage(display, drawable, 0, 0, (uint)Display.Width, (uint)Display.Height, X11.ALL_PLANES, X11.ZPIXMAP);
                if (image.IsInvalid || (display.LastError != null))
                    throw new InvalidOperationException($"XGetImage of the {Display.Width}x{Display.Height} screen failed{FormatXError(display.LastError)}.");

                image.ReadDescription();
                if (!image.IsBGRA) throw new NotSupportedException($"The X server uses an unsupported pixel format ({image.Format}). Only 32 bits per pixel with 8-bit BGR channels is supported.");

                _display = display;
                _drawable = drawable;
                Volatile.Write(ref _image, image);
                display = null;
                image = null;

                return true;
            }
            catch (Exception ex)
            {
                ReportError(ex);

                return false;
            }
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