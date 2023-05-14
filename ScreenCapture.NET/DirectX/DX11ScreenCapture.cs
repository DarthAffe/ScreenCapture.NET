using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
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
public sealed class DX11ScreenCapture : IScreenCapture
{
    #region Constants

    private static readonly FeatureLevel[] FEATURE_LEVELS =
    {
        FeatureLevel.Level_11_1,
        FeatureLevel.Level_11_0,
        FeatureLevel.Level_10_1,
        FeatureLevel.Level_10_0
    };

    private const int BPP = 4;

    #endregion

    #region Properties & Fields

    private readonly object _captureLock = new();

    private readonly bool _useNewDuplicationAdapter;
    private int _indexCounter = 0;

    /// <inheritdoc />
    public Display Display { get; }

    /// <summary>
    /// Gets or sets the timeout in ms used for screen-capturing. (default 1000ms)
    /// This is used in <see cref="CaptureScreen"/> https://docs.microsoft.com/en-us/windows/win32/api/dxgi1_2/nf-dxgi1_2-idxgioutputduplication-acquirenextframe
    /// </summary>
    // ReSharper disable once MemberCanBePrivate.Global
    public int Timeout { get; set; } = 1000;

    private readonly IDXGIFactory1 _factory;

    private IDXGIOutput? _output;
    private IDXGIOutputDuplication? _duplicatedOutput;
    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private ID3D11Texture2D? _captureTexture;

    private readonly Dictionary<CaptureZone, (ID3D11Texture2D stagingTexture, ID3D11Texture2D? scalingTexture, ID3D11ShaderResourceView? _scalingTextureView)> _captureZones = new();

    #endregion

    #region Events

    /// <inheritdoc />
    public event EventHandler<ScreenCaptureUpdatedEventArgs>? Updated;

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
    public DX11ScreenCapture(IDXGIFactory1 factory, Display display, bool useNewDuplicationAdapter = false)
    {
        this._factory = factory;
        this.Display = display;
        this._useNewDuplicationAdapter = useNewDuplicationAdapter;

        Restart();
    }

    #endregion

    #region Methods

    /// <inheritdoc />
    public bool CaptureScreen()
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
            catch { /**/ }

            try
            {
                UpdateZones();
            }
            catch { /**/ }

            try
            {
                Updated?.Invoke(this, new ScreenCaptureUpdatedEventArgs(result));
            }
            catch { /**/ }

            return result;
        }
    }

    private void UpdateZones()
    {
        if (_context == null) return;

        lock (_captureZones)
        {
            foreach ((CaptureZone captureZone, (ID3D11Texture2D stagingTexture, ID3D11Texture2D? scalingTexture, ID3D11ShaderResourceView? scalingTextureView)) in _captureZones.Where(z => z.Key.AutoUpdate || z.Key.IsUpdateRequested))
            {
                if (scalingTexture != null)
                {
                    _context.CopySubresourceRegion(scalingTexture, 0, 0, 0, 0, _captureTexture, 0,
                                                   new Box(captureZone.X, captureZone.Y, 0,
                                                           captureZone.X + captureZone.UnscaledWidth,
                                                           captureZone.Y + captureZone.UnscaledHeight, 1));
                    _context.GenerateMips(scalingTextureView);
                    _context.CopySubresourceRegion(stagingTexture, 0, 0, 0, 0, scalingTexture, captureZone.DownscaleLevel);
                }
                else
                    _context.CopySubresourceRegion(stagingTexture, 0, 0, 0, 0, _captureTexture, 0,
                                                   new Box(captureZone.X, captureZone.Y, 0,
                                                           captureZone.X + captureZone.UnscaledWidth,
                                                           captureZone.Y + captureZone.UnscaledHeight, 1));

                MappedSubresource mapSource = _context.Map(stagingTexture, 0, MapMode.Read, MapFlags.None);
                lock (captureZone.Buffer)
                {
                    Span<byte> source = mapSource.AsSpan(mapSource.RowPitch * captureZone.Height);
                    switch (Display.Rotation)
                    {
                        case Rotation.Rotation90:
                            CopyRotate90(source, mapSource.RowPitch, captureZone);
                            break;

                        case Rotation.Rotation180:
                            CopyRotate180(source, mapSource.RowPitch, captureZone);
                            break;

                        case Rotation.Rotation270:
                            CopyRotate270(source, mapSource.RowPitch, captureZone);
                            break;

                        default:
                            CopyRotate0(source, mapSource.RowPitch, captureZone);
                            break;
                    }
                }

                _context.Unmap(stagingTexture, 0);
                captureZone.SetUpdated();
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void CopyRotate0(in Span<byte> source, int sourceStride, in CaptureZone captureZone)
    {
        int height = captureZone.Height;
        int stride = captureZone.Stride;
        Span<byte> target = captureZone.Buffer.AsSpan();

        for (int y = 0; y < height; y++)
        {
            int sourceOffset = y * sourceStride;
            int targetOffset = y * stride;

            source.Slice(sourceOffset, stride).CopyTo(target.Slice(targetOffset, stride));
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void CopyRotate90(in Span<byte> source, int sourceStride, in CaptureZone captureZone)
    {
        int width = captureZone.Width;
        int height = captureZone.Height;
        Span<byte> target = captureZone.Buffer.AsSpan();

        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int sourceOffset = ((y * sourceStride) + (x * BPP));
                int targetOffset = ((x * height) + ((height - 1) - y)) * BPP;

                target[targetOffset] = source[sourceOffset];
                target[targetOffset + 1] = source[sourceOffset + 1];
                target[targetOffset + 2] = source[sourceOffset + 2];
                target[targetOffset + 3] = source[sourceOffset + 3];
            }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void CopyRotate180(in Span<byte> source, int sourceStride, in CaptureZone captureZone)
    {
        int width = captureZone.Width;
        int height = captureZone.Height;
        int stride = captureZone.Stride;
        Span<byte> target = captureZone.Buffer.AsSpan();

        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int sourceOffset = ((y * sourceStride) + (x * BPP));
                int targetOffset = target.Length - ((y * stride) + (x * BPP)) - 1;

                target[targetOffset - 3] = source[sourceOffset];
                target[targetOffset - 2] = source[sourceOffset + 1];
                target[targetOffset - 1] = source[sourceOffset + 2];
                target[targetOffset] = source[sourceOffset + 3];
            }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void CopyRotate270(in Span<byte> source, int sourceStride, in CaptureZone captureZone)
    {
        int width = captureZone.Width;
        int height = captureZone.Height;
        Span<byte> target = captureZone.Buffer.AsSpan();

        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int sourceOffset = ((y * sourceStride) + (x * BPP));
                int targetOffset = ((((width - 1) - x) * height) + y) * BPP;

                target[targetOffset] = source[sourceOffset];
                target[targetOffset + 1] = source[sourceOffset + 1];
                target[targetOffset + 2] = source[sourceOffset + 2];
                target[targetOffset + 3] = source[sourceOffset + 3];
            }
    }

    /// <inheritdoc />
    public CaptureZone RegisterCaptureZone(int x, int y, int width, int height, int downscaleLevel = 0)
    {
        ValidateCaptureZoneAndThrow(x, y, width, height);

        if (Display.Rotation is Rotation.Rotation90 or Rotation.Rotation270)
            (x, y, width, height) = (y, x, height, width);

        int unscaledWidth = width;
        int unscaledHeight = height;
        (width, height) = CalculateScaledSize(unscaledWidth, unscaledHeight, downscaleLevel);

        byte[] buffer = new byte[width * height * BPP];

        CaptureZone captureZone = new(_indexCounter++, x, y, width, height, BPP, downscaleLevel, unscaledWidth, unscaledHeight, buffer);
        lock (_captureZones)
            InitializeCaptureZone(captureZone);

        return captureZone;
    }

    /// <inheritdoc />
    public bool UnregisterCaptureZone(CaptureZone captureZone)
    {
        lock (_captureZones)
        {
            if (_captureZones.TryGetValue(captureZone, out (ID3D11Texture2D stagingTexture, ID3D11Texture2D? scalingTexture, ID3D11ShaderResourceView? _scalingTextureView) data))
            {
                _captureZones.Remove(captureZone);
                data.stagingTexture.Dispose();
                data.scalingTexture?.Dispose();
                data._scalingTextureView?.Dispose();

                return true;
            }

            return false;
        }
    }

    /// <inheritdoc />
    public void UpdateCaptureZone(CaptureZone captureZone, int? x = null, int? y = null, int? width = null, int? height = null, int? downscaleLevel = null)
    {
        lock (_captureZones)
            if (!_captureZones.ContainsKey(captureZone))
                throw new ArgumentException("The capture zone is not registered to this ScreenCapture", nameof(captureZone));

        int newX = x ?? captureZone.X;
        int newY = y ?? captureZone.Y;
        int newUnscaledWidth = width ?? captureZone.UnscaledWidth;
        int newUnscaledHeight = height ?? captureZone.UnscaledHeight;
        int newDownscaleLevel = downscaleLevel ?? captureZone.DownscaleLevel;

        ValidateCaptureZoneAndThrow(newX, newY, newUnscaledWidth, newUnscaledHeight);

        if (Display.Rotation is Rotation.Rotation90 or Rotation.Rotation270)
            (newX, newY, newUnscaledWidth, newUnscaledHeight) = (newY, newX, newUnscaledHeight, newUnscaledWidth);

        captureZone.X = newX;
        captureZone.Y = newY;

        //TODO DarthAffe 01.05.2022: For now just reinitialize the zone in that case, but this could be optimized to only recreate the textures needed.
        if ((width != null) || (height != null) || (downscaleLevel != null))
        {
            (int newWidth, int newHeight) = CalculateScaledSize(newUnscaledWidth, newUnscaledHeight, newDownscaleLevel);
            lock (_captureZones)
            {
                UnregisterCaptureZone(captureZone);

                captureZone.UnscaledWidth = newUnscaledWidth;
                captureZone.UnscaledHeight = newUnscaledHeight;
                captureZone.Width = newWidth;
                captureZone.Height = newHeight;
                captureZone.DownscaleLevel = newDownscaleLevel;
                captureZone.Buffer = new byte[newWidth * newHeight * BPP];

                InitializeCaptureZone(captureZone);
            }
        }
    }

    private (int width, int height) CalculateScaledSize(int width, int height, int downscaleLevel)
    {
        if (downscaleLevel > 0)
            for (int i = 0; i < downscaleLevel; i++)
            {
                width /= 2;
                height /= 2;
            }

        if (width < 1) width = 1;
        if (height < 1) height = 1;

        return (width, height);
    }

    private void ValidateCaptureZoneAndThrow(int x, int y, int width, int height)
    {
        if (_device == null) throw new ApplicationException("ScreenCapture isn't initialized.");

        if (x < 0) throw new ArgumentException("x < 0");
        if (y < 0) throw new ArgumentException("y < 0");
        if (width <= 0) throw new ArgumentException("with <= 0");
        if (height <= 0) throw new ArgumentException("height <= 0");
        if ((x + width) > Display.Width) throw new ArgumentException("x + width > Display width");
        if ((y + height) > Display.Height) throw new ArgumentException("y + height > Display height");
    }

    private void InitializeCaptureZone(in CaptureZone captureZone)
    {
        Texture2DDescription stagingTextureDesc = new()
        {
            CPUAccessFlags = CpuAccessFlags.Read,
            BindFlags = BindFlags.None,
            Format = Format.B8G8R8A8_UNorm,
            Width = captureZone.Width,
            Height = captureZone.Height,
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
                Width = captureZone.UnscaledWidth,
                Height = captureZone.UnscaledHeight,
                MiscFlags = ResourceOptionFlags.GenerateMips,
                MipLevels = captureZone.DownscaleLevel + 1,
                ArraySize = 1,
                SampleDescription = { Count = 1, Quality = 0 },
                Usage = ResourceUsage.Default
            };
            scalingTexture = _device!.CreateTexture2D(scalingTextureDesc);
            scalingTextureView = _device.CreateShaderResourceView(scalingTexture);
        }

        _captureZones[captureZone] = (stagingTexture, scalingTexture, scalingTextureView);
    }

    /// <inheritdoc />
    public void Restart()
    {
        lock (_captureLock)
        {
            try
            {
                List<CaptureZone> captureZones = _captureZones.Keys.ToList();
                Dispose();

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

                lock (_captureZones)
                {
                    foreach (CaptureZone captureZone in captureZones)
                        InitializeCaptureZone(captureZone);
                }

                if (_useNewDuplicationAdapter)
                    _duplicatedOutput = output.DuplicateOutput1(_device, new[] { Format.B8G8R8A8_UNorm }); // DarthAffe 27.02.2021: This prepares for the use of 10bit color depth
                else
                    _duplicatedOutput = output.DuplicateOutput(_device);
            }
            catch { Dispose(false); }
        }
    }

    /// <inheritdoc />
    public void Dispose() => Dispose(true);

    private void Dispose(bool removeCaptureZones)
    {
        try
        {
            lock (_captureLock)
            {
                try { _duplicatedOutput?.Dispose(); } catch { /**/ }
                _duplicatedOutput = null;

                try
                {
                    if (removeCaptureZones)
                    {
                        List<CaptureZone> captureZones = _captureZones.Keys.ToList();
                        foreach (CaptureZone captureZone in captureZones)
                            UnregisterCaptureZone(captureZone);
                    }
                }
                catch { /**/ }

                try { _output?.Dispose(); } catch { /**/ }
                try { _context?.Dispose(); } catch { /**/ }
                try { _device?.Dispose(); } catch { /**/ }
                try { _captureTexture?.Dispose(); } catch { /**/ }
                _context = null;
                _captureTexture = null;
            }
        }
        catch { /**/ }
    }

    #endregion
}