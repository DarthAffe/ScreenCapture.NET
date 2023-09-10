using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using ScreenCapture.NET.Downscale;
using SharpGen.Runtime;
using Vortice.Direct3D9;

namespace ScreenCapture.NET;

/// <summary>
/// Represents a ScreenCapture using DirectX 9.
/// https://learn.microsoft.com/en-us/windows/win32/api/d3d9/nf-d3d9-idirect3ddevice9-getfrontbufferdata
/// </summary>
// ReSharper disable once InconsistentNaming
public sealed class DX9ScreenCapture : AbstractScreenCapture<ColorBGRA>
{
    #region Properties & Fields

    private readonly object _captureLock = new();

    private readonly IDirect3D9 _direct3D9;
    private IDirect3DDevice9? _device;
    private IDirect3DSurface9? _surface;
    private byte[]? _buffer;
    private int _stride;

    #endregion

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="DX9ScreenCapture"/> class.
    /// </summary>
    /// <param name="direct3D9">The D3D9 instance used.</param>
    /// <param name="display">The <see cref="Display"/> to duplicate.</param>
    internal DX9ScreenCapture(IDirect3D9 direct3D9, Display display)
        : base(display)
    {
        this._direct3D9 = direct3D9;

        Restart();
    }

    #endregion

    #region Methods

    /// <inheritdoc />
    protected override bool PerformScreenCapture()
    {
        bool result = false;
        lock (_captureLock)
        {
            if ((_device == null) || (_surface == null) || (_buffer == null))
            {
                Restart();
                return false;
            }

            try
            {
                _device.GetFrontBufferData(0, _surface);

                LockedRectangle dr = _surface.LockRect(LockFlags.NoSystemLock | LockFlags.ReadOnly);

                nint ptr = dr.DataPointer;
                for (int y = 0; y < Display.Height; y++)
                {
                    Marshal.Copy(ptr, _buffer, y * _stride, _stride);
                    ptr += dr.Pitch;
                }

                _surface.UnlockRect();

                result = true;
            }
            catch (SharpGenException dxException)
            {
                if (dxException.ResultCode == Result.AccessDenied)
                {
                    try
                    {
                        Restart();
                    }
                    catch { Thread.Sleep(100); }
                }
            }
        }

        return result;
    }

    /// <inheritdoc />
    protected override void PerformCaptureZoneUpdate(CaptureZone<ColorBGRA> captureZone, in Span<byte> buffer)
    {
        if (_buffer == null) return;

        using IDisposable @lock = captureZone.Lock();
        {
            if (captureZone.DownscaleLevel == 0)
                CopyZone(captureZone, buffer);
            else
                DownscaleZone(captureZone, buffer);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void CopyZone(CaptureZone<ColorBGRA> captureZone, in Span<byte> buffer)
    {
        ReadOnlySpan<ColorBGRA> source = MemoryMarshal.Cast<byte, ColorBGRA>(_buffer);
        Span<ColorBGRA> target = MemoryMarshal.Cast<byte, ColorBGRA>(buffer);

        int offsetX = captureZone.X;
        int offsetY = captureZone.Y;
        int width = captureZone.Width;
        int height = captureZone.Height;

        for (int y = 0; y < height; y++)
        {
            int sourceOffset = ((y + offsetY) * Display.Width) + offsetX;
            int targetOffset = y * width;

            source.Slice(sourceOffset, width).CopyTo(target.Slice(targetOffset, width));
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void DownscaleZone(CaptureZone<ColorBGRA> captureZone, in Span<byte> buffer)
    {
        ReadOnlySpan<byte> source = _buffer;
        Span<byte> target = buffer;

        int blockSize = captureZone.DownscaleLevel switch
        {
            1 => 2,
            2 => 4,
            3 => 8,
            4 => 16,
            5 => 32,
            6 => 64,
            7 => 128,
            8 => 256,
            _ => (int)Math.Pow(2, captureZone.DownscaleLevel),
        };

        int offsetX = captureZone.X;
        int offsetY = captureZone.Y;
        int width = captureZone.Width;
        int height = captureZone.Height;
        int stride = captureZone.Stride;
        int bpp = captureZone.ColorFormat.BytesPerPixel;
        int unscaledWith = captureZone.UnscaledWidth;

        Span<byte> scaleBuffer = stackalloc byte[bpp];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                AverageByteSampler.Sample(new SamplerInfo<byte>((x + offsetX) * blockSize, (y + offsetY) * blockSize, blockSize, blockSize, unscaledWith, bpp, source), scaleBuffer);

                int targetOffset = (y * stride) + (x * bpp);

                // DarthAffe 07.09.2023: Unroll as optimization since we know it's always 4 bpp - not ideal but it does quite a lot
                target[targetOffset] = scaleBuffer[0];
                target[targetOffset + 1] = scaleBuffer[1];
                target[targetOffset + 2] = scaleBuffer[2];
                target[targetOffset + 3] = scaleBuffer[3];
            }
    }

    /// <inheritdoc />
    public override void Restart()
    {
        base.Restart();

        lock (_captureLock)
        {
            DisposeDX();

            try
            {
                PresentParameters presentParameters = new()
                {
                    BackBufferWidth = Display.Width,
                    BackBufferHeight = Display.Height,
                    Windowed = true,
                    SwapEffect = SwapEffect.Discard
                };
                _device = _direct3D9.CreateDevice(Display.Index, DeviceType.Hardware, IntPtr.Zero, CreateFlags.SoftwareVertexProcessing, presentParameters);
                _surface = _device.CreateOffscreenPlainSurface(Display.Width, Display.Height, Format.A8R8G8B8, Pool.Scratch);
                _stride = Display.Width * ColorBGRA.ColorFormat.BytesPerPixel;
                _buffer = new byte[Display.Height * _stride];
            }
            catch
            {
                DisposeDX();
            }
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
            try { _surface?.Dispose(); } catch { /**/}
            try { _device?.Dispose(); } catch { /**/}
            _buffer = null;
            _device = null;
            _surface = null;
        }
        catch { /**/ }
    }

    #endregion
}