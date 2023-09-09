using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ScreenCapture.NET.Downscale;

namespace ScreenCapture.NET;

/// <summary>
/// Represents a ScreenCapture using DirectX 11 desktop duplicaton.
/// https://docs.microsoft.com/en-us/windows/win32/direct3ddxgi/desktop-dup-api
/// </summary>
// ReSharper disable once InconsistentNaming
public sealed class X11ScreenCapture : AbstractScreenCapture<ColorBGRA>
{
    #region Properties & Fields

    private readonly object _captureLock = new();

    private nint _display;

    private readonly Dictionary<CaptureZone<ColorBGRA>, ZoneTextures> _textures = new();

    #endregion

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="X11ScreenCapture"/> class.
    /// </summary>
    /// <param name="display">The <see cref="Display"/> to duplicate.</param>
    public X11ScreenCapture(Display display)
        : base(display)
    {
        Restart();
    }

    #endregion

    #region Methods

    protected override bool PerformScreenCapture()
    {
        lock (_captureLock)
        {
            if (_display == 0)
            {
                Restart();
                return false;
            }

            lock (CaptureZones)
                lock (_textures)
                    foreach (CaptureZone<ColorBGRA> captureZone in CaptureZones)
                    {
                        if (!_textures.TryGetValue(captureZone, out ZoneTextures? textures)) break;
                        textures.Update();
                    }

            return true;
        }
    }

    protected override void PerformCaptureZoneUpdate(CaptureZone<ColorBGRA> captureZone, in Span<byte> buffer)
    {
        lock (_textures)
        {
            using IDisposable @lock = captureZone.Lock();
            {
                if (captureZone.DownscaleLevel == 0)
                    CopyZone(captureZone, buffer);
                else
                    DownscaleZone(captureZone, buffer);
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void CopyZone(CaptureZone<ColorBGRA> captureZone, in Span<byte> buffer)
    {
        if (!_textures.TryGetValue(captureZone, out ZoneTextures? textures)) return;

        ReadOnlySpan<ColorBGRA> source = MemoryMarshal.Cast<byte, ColorBGRA>(textures.Data);
        Span<ColorBGRA> target = MemoryMarshal.Cast<byte, ColorBGRA>(buffer);

        int width = captureZone.Width;
        int height = captureZone.Height;
        int sourceStride = textures.Image.bytes_per_line / ColorBGRA.ColorFormat.BytesPerPixel;

        for (int y = 0; y < height; y++)
        {
            int sourceOffset = y * sourceStride;
            int targetOffset = y * width;
            source.Slice(sourceOffset, width).CopyTo(target.Slice(targetOffset, width));
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void DownscaleZone(CaptureZone<ColorBGRA> captureZone, in Span<byte> buffer)
    {
        if (!_textures.TryGetValue(captureZone, out ZoneTextures? textures)) return;

        ReadOnlySpan<byte> source = textures.Data;
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

        int width = captureZone.Width;
        int height = captureZone.Height;
        int stride = captureZone.Stride;
        int bpp = captureZone.ColorFormat.BytesPerPixel;
        int sourceStride = textures.Image.bytes_per_line / ColorBGRA.ColorFormat.BytesPerPixel;

        Span<byte> scaleBuffer = stackalloc byte[bpp];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                AverageByteSampler.Sample(new SamplerInfo<byte>(x * blockSize, y * blockSize, blockSize, blockSize, sourceStride, bpp, source), scaleBuffer);

                int targetOffset = (y * stride) + (x * bpp);

                // DarthAffe 09.09.2023: Unroll as optimization since we know it's always 4 bpp - not ideal but it does quite a lot
                target[targetOffset] = scaleBuffer[0];
                target[targetOffset + 1] = scaleBuffer[1];
                target[targetOffset + 2] = scaleBuffer[2];
                target[targetOffset + 3] = scaleBuffer[3];
            }
    }

    /// <inheritdoc />
    public override CaptureZone<ColorBGRA> RegisterCaptureZone(int x, int y, int width, int height, int downscaleLevel = 0)
    {
        CaptureZone<ColorBGRA> captureZone = base.RegisterCaptureZone(x, y, width, height, downscaleLevel);

        lock (_textures)
            InitializeCaptureZone(captureZone);

        return captureZone;
    }

    /// <inheritdoc />
    public override bool UnregisterCaptureZone(CaptureZone<ColorBGRA> captureZone)
    {
        if (!base.UnregisterCaptureZone(captureZone)) return false;

        lock (_textures)
        {
            if (_textures.TryGetValue(captureZone, out ZoneTextures? textures))
            {
                textures.Dispose();
                _textures.Remove(captureZone);

                return true;
            }

            return false;
        }
    }

    /// <inheritdoc />
    public override void UpdateCaptureZone(CaptureZone<ColorBGRA> captureZone, int? x = null, int? y = null, int? width = null, int? height = null, int? downscaleLevel = null)
    {
        base.UpdateCaptureZone(captureZone, x, y, width, height, downscaleLevel);

        if ((width != null) || (height != null))
        {
            lock (_textures)
            {
                if (_textures.TryGetValue(captureZone, out ZoneTextures? textures))
                {
                    textures.Dispose();
                    InitializeCaptureZone(captureZone);
                }
            }
        }
    }

    private void InitializeCaptureZone(in CaptureZone<ColorBGRA> captureZone)
        => _textures[captureZone] = new ZoneTextures(_display, captureZone.Display.Index, captureZone.X, captureZone.Y, captureZone.UnscaledWidth, captureZone.UnscaledHeight);

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
            }
            catch
            {
                DisposeDisplay();
            }
        }
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        lock (_captureLock)
        {
            try
            {
                foreach ((CaptureZone<ColorBGRA> _, ZoneTextures textures) in _textures)
                    textures.Dispose();

                DisposeDisplay();
            }
            catch { /**/ }
        }
    }

    private void DisposeDisplay()
    {
        if (_display == 0) return;

        try { X11.XCloseDisplay(_display); } catch { /**/ }
    }

    #endregion

    private sealed class ZoneTextures : IDisposable
    {
        #region Properties & Fields

        private readonly int _screenNumber;
        private readonly int _x;
        private readonly int _y;
        private readonly uint _width;
        private readonly uint _height;
        private readonly int _size;

        private nint _display;
        private nint _drawable;
        public nint ImageHandle { get; private set; }
        public X11.XImage Image { get; private set; }
        public unsafe ReadOnlySpan<byte> Data => new(Image.data, _size);

        #endregion

        #region Constructors

        public ZoneTextures(nint display, int screenNumber, int x, int y, int width, int height)
        {
            this._screenNumber = screenNumber;
            this._x = x;
            this._y = y;
            this._width = (uint)width;
            this._height = (uint)height;

            _size = width * height * ColorBGRA.ColorFormat.BytesPerPixel;
            Initialize(display);
        }

        #endregion

        #region Methods

        public void Initialize(nint display)
        {
            Dispose();

            _display = display;

            nint screen = X11.XScreenOfDisplay(_display, _screenNumber);
            _drawable = X11.XRootWindowOfScreen(screen);
            ImageHandle = X11.XGetImage(display, _drawable, _x, _y, _width, _height, X11.ALL_PLANES, X11.ZPIXMAP);
            Image = Marshal.PtrToStructure<X11.XImage>(ImageHandle);

            if (Image.bits_per_pixel != (ColorBGRA.ColorFormat.BytesPerPixel * 8)) throw new NotSupportedException("The X-Server is configured to a not supported pixel-format. Needs to be 32 bit per pixel BGR.");
        }

        public void Update() => X11.XGetSubImage(_display, _drawable, _x, _y, _width, _height, X11.ALL_PLANES, X11.ZPIXMAP, ImageHandle, 0, 0);

        public void Dispose()
        {
            if (ImageHandle != 0)
                try { X11.XDestroyImage(ImageHandle); } catch { /**/ }

            Image = default;
        }

        #endregion
    }
}