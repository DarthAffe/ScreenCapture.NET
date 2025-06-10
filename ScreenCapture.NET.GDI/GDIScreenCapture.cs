using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using HPPH;
using HPPH.System.Drawing;

namespace ScreenCapture.NET;

/// <summary>
/// Represents a ScreenCapture using GDI
/// </summary>
// ReSharper disable once InconsistentNaming
public sealed partial class GDIScreenCapture : AbstractScreenCapture<ColorBGRA>
{
    #region DLL-Import

    private const int SRCCOPY = 0x00CC0020;

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool BitBlt(nint hdcDest, int xDest, int yDest, int w, int h, nint hdcSrc, int xSrc, int ySrc, int rop);

    [LibraryImport("user32.dll")]
    private static partial nint GetDC(nint hwnd);

    [LibraryImport("user32.dll")]
    private static partial int ReleaseDC(nint hwnd, nint hdc);

    #endregion

    #region Properties & Fields

    private readonly Lock _captureLock = new();

    private Bitmap? _bitmap;

    #endregion

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="GDIScreenCapture"/> class.
    /// </summary>
    /// <param name="display">The <see cref="Display"/> to duplicate.</param>
    internal GDIScreenCapture(Display display)
        : base(display)
    {
        Restart();
    }

    #endregion

    #region Methods

    /// <inheritdoc />
    protected override unsafe bool PerformScreenCapture()
    {
        lock (_captureLock)
        {
            if (_bitmap == null) return false;

            using Graphics gDest = Graphics.FromImage(_bitmap);
            nint dc = 0;
            nint dest = 0;

            try
            {
                //dc = GetDC(nint.Zero);
                //dest = gDest.GetHdc();
                Stopwatch sw = Stopwatch.StartNew();
                gDest.CopyFromScreen(0, 0, 0, 0, new Size(Display.Width, 2));
                //BitBlt(dest, 0, 0, Display.Width, Display.Height, dc, 0, 0, SRCCOPY);
                Console.WriteLine(sw.Elapsed.TotalMilliseconds);
            }
            catch
            {
                return false;
            }
            finally
            {
                gDest.ReleaseHdc(dest);

                if (dc != nint.Zero)
                    ReleaseDC(nint.Zero, dc);
            }
        }

        return true;
    }

    /// <inheritdoc />
    protected override void PerformCaptureZoneUpdate(CaptureZone<ColorBGRA> captureZone, Span<byte> buffer)
    {
        if (_bitmap == null) return;

        using IDisposable @lock = captureZone.Lock();

        if (captureZone.DownscaleLevel == 0)
            CopyZone(captureZone, buffer);
        else
            DownscaleZone(captureZone, buffer);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private unsafe void CopyZone(CaptureZone<ColorBGRA> captureZone, Span<byte> buffer)
    {
        if (_bitmap == null) return;

        BitmapData data = _bitmap.LockBits(new Rectangle(0, 0, _bitmap.Width, _bitmap.Height), ImageLockMode.ReadOnly, _bitmap.PixelFormat);
        ReadOnlySpan<byte> bitmapBuffer = new(data.Scan0.ToPointer(), data.Stride * data.Height);

        RefImage<ColorBGRA>.Wrap(bitmapBuffer, Display.Width, Display.Height, data.Stride)[captureZone.X, captureZone.Y, captureZone.Width, captureZone.Height]
                           .CopyTo(MemoryMarshal.Cast<byte, ColorBGRA>(buffer));

        _bitmap.UnlockBits(data);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private unsafe void DownscaleZone(CaptureZone<ColorBGRA> captureZone, Span<byte> buffer)
    {
        BitmapData data = _bitmap.LockBits(new Rectangle(0, 0, _bitmap.Width, _bitmap.Height), ImageLockMode.ReadOnly, _bitmap.PixelFormat);
        ReadOnlySpan<byte> bitmapBuffer = new(data.Scan0.ToPointer(), data.Stride * data.Height);

        RefImage<ColorBGRA> source = RefImage<ColorBGRA>.Wrap(bitmapBuffer, Display.Width, Display.Height, data.Stride)[captureZone.X, captureZone.Y, captureZone.UnscaledWidth, captureZone.UnscaledHeight];
        Span<ColorBGRA> target = MemoryMarshal.Cast<byte, ColorBGRA>(buffer);

        int blockSize = 1 << captureZone.DownscaleLevel;

        int width = captureZone.Width;
        int height = captureZone.Height;

        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                target[(y * width) + x] = source[x * blockSize, y * blockSize, blockSize, blockSize].Average();

        _bitmap.UnlockBits(data);
    }

    /// <inheritdoc />
    public override void Restart()
    {
        base.Restart();

        lock (_captureLock)
        {
            _bitmap?.Dispose();
            _bitmap = new Bitmap(Display.Width, Display.Height, PixelFormat.Format32bppArgb);
        }
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        lock (_captureLock)
            DisposeDX();
    }

    private void DisposeDX()
    {
        try
        {
            _bitmap?.Dispose();
            _bitmap = null;
        }
        catch { /**/ }
    }

    #endregion
}