using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using MapFlags = Vortice.Direct3D11.MapFlags;
using ResultCode = Vortice.DXGI.ResultCode;

namespace ScreenCapture.NET;

/// <summary>
/// Represents a ScreenCapture using DirectX 11 desktop duplicaton.
/// https://docs.microsoft.com/en-us/windows/win32/direct3ddxgi/desktop-dup-api
/// </summary>
// ReSharper disable once InconsistentNaming
public sealed class DX11ScreenCapture : AbstractScreenCapture<ColorBGRA>
{
    #region Constants

    private static readonly FeatureLevel[] FEATURE_LEVELS =
    {
        FeatureLevel.Level_11_1,
        FeatureLevel.Level_11_0,
        FeatureLevel.Level_10_1,
        FeatureLevel.Level_10_0
    };

    #endregion

    #region Properties & Fields

    private readonly object _captureLock = new();

    private readonly bool _useNewDuplicationAdapter;

    /// <summary>
    /// Gets or sets the timeout in ms used for screen-capturing. (default 1000ms)
    /// This is used in <see cref="PerformScreenCapture"/> https://docs.microsoft.com/en-us/windows/win32/api/dxgi1_2/nf-dxgi1_2-idxgioutputduplication-acquirenextframe
    /// </summary>
    // ReSharper disable once MemberCanBePrivate.Global
    public int Timeout { get; set; } = 1000;

    private readonly IDXGIFactory1 _factory;

    private IDXGIOutput? _output;
    private IDXGIOutputDuplication? _duplicatedOutput;
    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private ID3D11Texture2D? _captureTexture;

    private readonly Dictionary<CaptureZone<ColorBGRA>, ZoneTextures> _textures = new();

    #endregion

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="DX11ScreenCapture"/> class.
    /// </summary>
    /// <remarks>
    /// Note that setting useNewDuplicationAdapter to true requires to call <c>DPIAwareness.Initalize();</c> and prevents the capture from running in a WPF-thread.
    /// </remarks>
    /// <param name="factory">The <see cref="IDXGIFactory1"/> used to create underlying objects.</param>
    /// <param name="display">The <see cref="Display"/> to duplicate.</param>
    /// <param name="useNewDuplicationAdapter">Indicates if the DuplicateOutput1 interface should be used instead of the older DuplicateOutput. Currently there's no real use in setting this to true.</param>
    internal DX11ScreenCapture(IDXGIFactory1 factory, Display display, bool useNewDuplicationAdapter = false)
        : base(display)
    {
        this._factory = factory;
        this._useNewDuplicationAdapter = useNewDuplicationAdapter;

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
            if ((_context == null) || (_duplicatedOutput == null) || (_captureTexture == null))
            {
                Restart();
                return false;
            }

            try
            {
                IDXGIResource? screenResource = null;
                try
                {
                    _duplicatedOutput.AcquireNextFrame(Timeout, out OutduplFrameInfo duplicateFrameInformation, out screenResource).CheckError();
                    if ((screenResource == null) || (duplicateFrameInformation.LastPresentTime == 0)) return false;

                    using ID3D11Texture2D screenTexture = screenResource.QueryInterface<ID3D11Texture2D>();
                    _context.CopySubresourceRegion(_captureTexture, 0, 0, 0, 0, screenTexture, 0);
                }
                finally
                {
                    try
                    {
                        screenResource?.Dispose();
                        _duplicatedOutput?.ReleaseFrame();
                    }
                    catch { /**/ }
                }

                result = true;
            }
            catch (SharpGenException dxException)
            {
                if ((dxException.ResultCode == ResultCode.AccessLost)
                 || (dxException.ResultCode == ResultCode.AccessDenied)
                 || (dxException.ResultCode == ResultCode.InvalidCall))
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
        if (_context == null) return;

        lock (_textures)
        {
            if (!_textures.TryGetValue(captureZone, out ZoneTextures? textures)) return;

            if (textures.ScalingTexture != null)
            {
                _context.CopySubresourceRegion(textures.ScalingTexture, 0, 0, 0, 0, _captureTexture, 0,
                                               new Box(textures.X, textures.Y, 0,
                                                       textures.X + textures.UnscaledWidth,
                                                       textures.Y + textures.UnscaledHeight, 1));
                _context.GenerateMips(textures.ScalingTextureView);
                _context.CopySubresourceRegion(textures.StagingTexture, 0, 0, 0, 0, textures.ScalingTexture, captureZone.DownscaleLevel);
            }
            else
                _context.CopySubresourceRegion(textures.StagingTexture, 0, 0, 0, 0, _captureTexture, 0,
                                               new Box(textures.X, textures.Y, 0,
                                                       textures.X + textures.UnscaledWidth,
                                                       textures.Y + textures.UnscaledHeight, 1));

            MappedSubresource mapSource = _context.Map(textures.StagingTexture, 0, MapMode.Read, MapFlags.None);

            using IDisposable @lock = captureZone.Lock();
            {
                ReadOnlySpan<byte> source = mapSource.AsSpan(mapSource.RowPitch * textures.Height);
                switch (Display.Rotation)
                {
                    case Rotation.Rotation90:
                        CopyRotate90(source, mapSource.RowPitch, captureZone, buffer);
                        break;

                    case Rotation.Rotation180:
                        CopyRotate180(source, mapSource.RowPitch, captureZone, buffer);
                        break;

                    case Rotation.Rotation270:
                        CopyRotate270(source, mapSource.RowPitch, captureZone, buffer);
                        break;

                    default:
                        CopyRotate0(source, mapSource.RowPitch, captureZone, buffer);
                        break;
                }
            }

            _context.Unmap(textures.StagingTexture, 0);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void CopyRotate0(in ReadOnlySpan<byte> source, int sourceStride, in CaptureZone<ColorBGRA> captureZone, in Span<byte> buffer)
    {
        int height = captureZone.Height;
        int stride = captureZone.Stride;
        Span<byte> target = buffer;

        for (int y = 0; y < height; y++)
        {
            int sourceOffset = y * sourceStride;
            int targetOffset = y * stride;

            source.Slice(sourceOffset, stride).CopyTo(target.Slice(targetOffset, stride));
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void CopyRotate90(in ReadOnlySpan<byte> source, int sourceStride, in CaptureZone<ColorBGRA> captureZone, in Span<byte> buffer)
    {
        int width = captureZone.Width;
        int height = captureZone.Height;
        int usedBytesPerLine = height * captureZone.ColorFormat.BytesPerPixel;
        Span<ColorBGRA> target = MemoryMarshal.Cast<byte, ColorBGRA>(buffer);

        for (int x = 0; x < width; x++)
        {
            ReadOnlySpan<ColorBGRA> src = MemoryMarshal.Cast<byte, ColorBGRA>(source.Slice(x * sourceStride, usedBytesPerLine));
            for (int y = 0; y < src.Length; y++)
                target[(y * width) + (width - x - 1)] = src[y];
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void CopyRotate180(in ReadOnlySpan<byte> source, int sourceStride, in CaptureZone<ColorBGRA> captureZone, in Span<byte> buffer)
    {
        int width = captureZone.Width;
        int height = captureZone.Height;
        int bpp = captureZone.ColorFormat.BytesPerPixel;
        int usedBytesPerLine = width * bpp;
        Span<ColorBGRA> target = MemoryMarshal.Cast<byte, ColorBGRA>(buffer);

        for (int y = 0; y < height; y++)
        {
            ReadOnlySpan<ColorBGRA> src = MemoryMarshal.Cast<byte, ColorBGRA>(source.Slice(y * sourceStride, usedBytesPerLine));
            for (int x = 0; x < src.Length; x++)
                target[((height - y - 1) * width) + (width - x - 1)] = src[x];
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void CopyRotate270(in ReadOnlySpan<byte> source, int sourceStride, in CaptureZone<ColorBGRA> captureZone, in Span<byte> buffer)
    {
        int width = captureZone.Width;
        int height = captureZone.Height;
        int usedBytesPerLine = height * captureZone.ColorFormat.BytesPerPixel;
        Span<ColorBGRA> target = MemoryMarshal.Cast<byte, ColorBGRA>(buffer);

        for (int x = 0; x < width; x++)
        {
            ReadOnlySpan<ColorBGRA> src = MemoryMarshal.Cast<byte, ColorBGRA>(source.Slice(x * sourceStride, usedBytesPerLine));
            for (int y = 0; y < src.Length; y++)
                target[((height - y - 1) * width) + x] = src[y];
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
                captureZone.Dispose();
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

        //TODO DarthAffe 01.05.2022: For now just reinitialize the zone in that case, but this could be optimized to only recreate the textures needed.
        if ((width != null) || (height != null) || (downscaleLevel != null))
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

    /// <inheritdoc />
    protected override void ValidateCaptureZoneAndThrow(int x, int y, int width, int height, int downscaleLevel)
    {
        if (_device == null) throw new ApplicationException("ScreenCapture isn't initialized.");

        base.ValidateCaptureZoneAndThrow(x, y, width, height, downscaleLevel);
    }

    private void InitializeCaptureZone(in CaptureZone<ColorBGRA> captureZone)
    {
        int x;
        int y;
        int width;
        int height;
        int unscaledWidth;
        int unscaledHeight;

        if (captureZone.Display.Rotation is Rotation.Rotation90 or Rotation.Rotation270)
        {
            x = captureZone.Y;
            y = captureZone.X;
            width = captureZone.Height;
            height = captureZone.Width;
            unscaledWidth = captureZone.UnscaledHeight;
            unscaledHeight = captureZone.UnscaledWidth;
        }
        else
        {
            x = captureZone.X;
            y = captureZone.Y;
            width = captureZone.Width;
            height = captureZone.Height;
            unscaledWidth = captureZone.UnscaledWidth;
            unscaledHeight = captureZone.UnscaledHeight;
        }

        Texture2DDescription stagingTextureDesc = new()
        {
            CPUAccessFlags = CpuAccessFlags.Read,
            BindFlags = BindFlags.None,
            Format = Format.B8G8R8A8_UNorm,
            Width = width,
            Height = height,
            MiscFlags = ResourceOptionFlags.None,
            MipLevels = 1,
            ArraySize = 1,
            SampleDescription = { Count = 1, Quality = 0 },
            Usage = ResourceUsage.Staging
        };
        ID3D11Texture2D stagingTexture = _device!.CreateTexture2D(stagingTextureDesc);

        ID3D11Texture2D? scalingTexture = null;
        ID3D11ShaderResourceView? scalingTextureView = null;
        if (captureZone.DownscaleLevel > 0)
        {
            Texture2DDescription scalingTextureDesc = new()
            {
                CPUAccessFlags = CpuAccessFlags.None,
                BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
                Format = Format.B8G8R8A8_UNorm,
                Width = unscaledWidth,
                Height = unscaledHeight,
                MiscFlags = ResourceOptionFlags.GenerateMips,
                MipLevels = captureZone.DownscaleLevel + 1,
                ArraySize = 1,
                SampleDescription = { Count = 1, Quality = 0 },
                Usage = ResourceUsage.Default
            };
            scalingTexture = _device!.CreateTexture2D(scalingTextureDesc);
            scalingTextureView = _device.CreateShaderResourceView(scalingTexture);
        }

        _textures[captureZone] = new ZoneTextures(x, y, width, height, unscaledWidth, unscaledHeight, stagingTexture, scalingTexture, scalingTextureView);
    }

    /// <inheritdoc />
    public override void Restart()
    {
        base.Restart();

        lock (_captureLock)
            lock (_textures)
            {
                try
                {
                    foreach (ZoneTextures textures in _textures.Values)
                        textures.Dispose();
                    _textures.Clear();

                    DisposeDX();

                    using IDXGIAdapter1 adapter = _factory.GetAdapter1(Display.GraphicsCard.Index) ?? throw new ApplicationException("Couldn't create DirectX-Adapter.");

                    D3D11.D3D11CreateDevice(adapter, DriverType.Unknown, DeviceCreationFlags.None, FEATURE_LEVELS, out _device).CheckError();
                    _context = _device!.ImmediateContext;

                    _output = adapter.GetOutput(Display.Index) ?? throw new ApplicationException("Couldn't get DirectX-Output.");
                    using IDXGIOutput5 output = _output.QueryInterface<IDXGIOutput5>();

                    int width = Display.Width;
                    int height = Display.Height;
                    if (Display.Rotation is Rotation.Rotation90 or Rotation.Rotation270)
                        (width, height) = (height, width);

                    Texture2DDescription captureTextureDesc = new()
                    {
                        CPUAccessFlags = CpuAccessFlags.None,
                        BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
                        Format = Format.B8G8R8A8_UNorm,
                        Width = width,
                        Height = height,
                        MiscFlags = ResourceOptionFlags.None,
                        MipLevels = 1,
                        ArraySize = 1,
                        SampleDescription = { Count = 1, Quality = 0 },
                        Usage = ResourceUsage.Default
                    };
                    _captureTexture = _device.CreateTexture2D(captureTextureDesc);

                    foreach (CaptureZone<ColorBGRA> captureZone in CaptureZones)
                        InitializeCaptureZone(captureZone);

                    if (_useNewDuplicationAdapter)
                        _duplicatedOutput = output.DuplicateOutput1(_device, new[] { Format.B8G8R8A8_UNorm }); // DarthAffe 27.02.2021: This prepares for the use of 10bit color depth
                    else
                        _duplicatedOutput = output.DuplicateOutput(_device);
                }
                catch { DisposeDX(); }
            }
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        lock (_captureLock)
        {
            foreach (ZoneTextures textures in _textures.Values)
                textures.Dispose();
            _textures.Clear();

            DisposeDX();
        }
    }

    private void DisposeDX()
    {
        try
        {
            try { _duplicatedOutput?.Dispose(); } catch { /**/ }
            try { _output?.Dispose(); } catch { /**/ }
            try { _context?.Dispose(); } catch { /**/ }
            try { _device?.Dispose(); } catch { /**/ }
            try { _captureTexture?.Dispose(); } catch { /**/ }

            _duplicatedOutput = null;
            _context = null;
            _captureTexture = null;
        }
        catch { /**/ }
    }

    #endregion

    private sealed class ZoneTextures : IDisposable
    {
        #region Properties & Fields

        public int X { get; }
        public int Y { get; }
        public int Width { get; }
        public int Height { get; }
        public int UnscaledWidth { get; }
        public int UnscaledHeight { get; }

        public ID3D11Texture2D StagingTexture { get; }
        public ID3D11Texture2D? ScalingTexture { get; }
        public ID3D11ShaderResourceView? ScalingTextureView { get; }

        #endregion

        #region Constructors

        public ZoneTextures(int x, int y, int width, int height, int unscaledWidth, int unscaledHeight,
                            ID3D11Texture2D stagingTexture, ID3D11Texture2D? scalingTexture, ID3D11ShaderResourceView? scalingTextureView)
        {
            this.X = x;
            this.Y = y;
            this.Width = width;
            this.Height = height;
            this.UnscaledWidth = unscaledWidth;
            this.UnscaledHeight = unscaledHeight;
            this.StagingTexture = stagingTexture;
            this.ScalingTexture = scalingTexture;
            this.ScalingTextureView = scalingTextureView;
        }

        #endregion

        #region Methods

        public void Dispose()
        {
            StagingTexture.Dispose();
            ScalingTexture?.Dispose();
            ScalingTextureView?.Dispose();
        }

        #endregion
    }
}