using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using MapFlags = Vortice.Direct3D11.MapFlags;
using ResultCode = Vortice.DXGI.ResultCode;
using Usage = Vortice.Direct3D11.Usage;

namespace ScreenCapture.NET
{
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

        #endregion

        #region Properties & Fields

        private readonly object _captureLock = new();

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
        /// <param name="factory">The <see cref="IDXGIFactory1"/> used to create underlying objects.</param>
        /// <param name="display">The <see cref="Display"/> to duplicate.</param>
        public DX11ScreenCapture(IDXGIFactory1 factory, Display display)
        {
            this._factory = factory;
            this.Display = display;

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
                        _duplicatedOutput.AcquireNextFrame(Timeout, out OutduplFrameInfo duplicateFrameInformation, out screenResource);
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
                    IntPtr sourcePtr = mapSource.DataPointer;
                    lock (captureZone.Buffer)
                        Marshal.Copy(sourcePtr, captureZone.Buffer, 0, captureZone.Buffer.Length);

                    _context.Unmap(stagingTexture, 0);
                    captureZone.SetUpdated();
                }
            }
        }

        /// <inheritdoc />
        public CaptureZone RegisterCaptureZone(int x, int y, int width, int height, int downscaleLevel = 0)
        {
            if (_device == null) throw new ApplicationException("ScreenCapture isn't initialized.");

            if (x < 0) throw new ArgumentException("x < 0");
            if (y < 0) throw new ArgumentException("y < 0");
            if (width <= 0) throw new ArgumentException("with <= 0");
            if (height <= 0) throw new ArgumentException("height <= 0");
            if ((x + width) > Display.Width) throw new ArgumentException("x + width > Display width");
            if ((y + height) > Display.Height) throw new ArgumentException("y + height > Display height");

            int textureWidth = (int)Math.Ceiling(width / 32.0) * 32;
            int textureHeight = (int)Math.Ceiling(height / 32.0) * 32;

            int unscaledWidth = width;
            int unscaledHeight = height;
            if (downscaleLevel > 0)
                for (int i = 0; i < downscaleLevel; i++)
                {
                    width /= 2;
                    height /= 2;
                }

            if (width < 1) width = 1;
            if (height < 1) height = 1;

            int bufferWidth = (int)Math.Ceiling(width / 32.0) * 32;
            int bufferHeight = (int)Math.Ceiling(height / 32.0) * 32;

            byte[] buffer = new byte[bufferWidth * bufferHeight * 4];

            CaptureZone captureZone = new(_indexCounter++, x, y, width, height, downscaleLevel, unscaledWidth, unscaledHeight, textureWidth, textureHeight, bufferWidth, bufferHeight, buffer);
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

        private void InitializeCaptureZone(in CaptureZone captureZone)
        {
            Texture2DDescription stagingTextureDesc = new()
            {
                CpuAccessFlags = CpuAccessFlags.Read,
                BindFlags = BindFlags.None,
                Format = Format.B8G8R8A8_UNorm,
                Width = captureZone.BufferWidth,
                Height = captureZone.BufferHeight,
                OptionFlags = ResourceOptionFlags.None,
                MipLevels = 1,
                ArraySize = 1,
                SampleDescription = { Count = 1, Quality = 0 },
                Usage = Usage.Staging
            };
            ID3D11Texture2D stagingTexture = _device!.CreateTexture2D(stagingTextureDesc);

            ID3D11Texture2D? scalingTexture = null;
            ID3D11ShaderResourceView? scalingTextureView = null;
            if (captureZone.DownscaleLevel > 0)
            {
                Texture2DDescription scalingTextureDesc = new()
                {
                    CpuAccessFlags = CpuAccessFlags.None,
                    BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
                    Format = Format.B8G8R8A8_UNorm,
                    Width = captureZone.CaptureWidth,
                    Height = captureZone.CaptureHeight,
                    OptionFlags = ResourceOptionFlags.GenerateMips,
                    MipLevels = captureZone.DownscaleLevel + 1,
                    ArraySize = 1,
                    SampleDescription = { Count = 1, Quality = 0 },
                    Usage = Usage.Default
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

                    using IDXGIAdapter1 adapter = _factory.GetAdapter1(Display.GraphicsCard.Index);

                    D3D11.D3D11CreateDevice(adapter, DriverType.Unknown, DeviceCreationFlags.None, FEATURE_LEVELS, out _device).CheckError();
                    _context = _device.ImmediateContext;

                    _output = adapter.GetOutput(Display.Index);
                    using IDXGIOutput5 output1 = _output.QueryInterface<IDXGIOutput5>();

                    Texture2DDescription captureTextureDesc = new()
                    {
                        CpuAccessFlags = CpuAccessFlags.None,
                        BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
                        Format = Format.B8G8R8A8_UNorm,
                        Width = Display.Width,
                        Height = Display.Height,
                        OptionFlags = ResourceOptionFlags.None,
                        MipLevels = 1,
                        ArraySize = 1,
                        SampleDescription = { Count = 1, Quality = 0 },
                        Usage = Usage.Default
                    };
                    _captureTexture = _device.CreateTexture2D(captureTextureDesc);

                    lock (_captureZones)
                    {
                        foreach (CaptureZone captureZone in captureZones)
                            InitializeCaptureZone(captureZone);
                    }

                    _duplicatedOutput = output1.DuplicateOutput1(_device, Format.B8G8R8A8_UNorm); // DarthAffe 27.02.2021: This prepares for the use of 10bit color depth
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
}
