using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using HPPH;
using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using MapFlags = Vortice.Direct3D11.MapFlags;
using ResultCode = Vortice.DXGI.ResultCode;

namespace ScreenCapture.NET;

/// <summary>
/// Represents a ScreenCapture using DirectX 11 desktop duplication.
/// https://docs.microsoft.com/en-us/windows/win32/direct3ddxgi/desktop-dup-api
/// </summary>
/// <remarks>
/// A display mode change (resolution, rotation) ends the duplication: the next capture restarts it and reads the display again (see <see cref="IScreenCapture.DisplayChanged"/>).
/// </remarks>
// ReSharper disable once InconsistentNaming
public sealed class DX11ScreenCapture : AbstractScreenCapture<ColorBGRA>
{
    #region Constants

    private static readonly FeatureLevel[] FEATURE_LEVELS =
    [
        FeatureLevel.Level_11_1,
        FeatureLevel.Level_11_0,
        FeatureLevel.Level_10_1,
        FeatureLevel.Level_10_0
    ];

    #endregion

    #region Properties & Fields

    // Guards the DirectX objects and the immediate context (which isn't thread-safe): held by captures, zone updates and restarts.
    private readonly Lock _captureLock = new();

    // Guards _textures. Acquired after _captureLock; Unregister takes only this one, so it doesn't wait for a capture waiting for a frame.
    private readonly Lock _texturesLock = new();

    private bool _isDisposed;

    /// <summary>
    /// Gets the options this capture was created with.
    /// </summary>
    public DX11CaptureOptions Options { get; }

    // In whole milliseconds; an int, so it's read and written atomically.
    private int _timeout;

    /// <summary>
    /// Gets or sets how long <see cref="IScreenCapture.CaptureScreen"/> waits for a new frame before it returns <see cref="CaptureResult.Timeout"/>.
    /// </summary>
    /// <remarks>
    /// It starts with <see cref="DX11CaptureOptions.Timeout"/> and can be changed at any time; a running capture keeps the value it started with.
    /// It's rounded up to whole milliseconds; 0 doesn't wait.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative (including <see cref="System.Threading.Timeout.InfiniteTimeSpan"/>) or greater than <see cref="DX11CaptureOptions.MaxTimeout"/>.</exception>
    public TimeSpan Timeout
    {
        get => TimeSpan.FromMilliseconds(_timeout);
        set => _timeout = DX11CaptureOptions.ToMilliseconds(DX11CaptureOptions.ValidateTimeout(value));
    }

    private readonly IDXGIFactory1 _factory;

    private IDXGIOutputDuplication? _duplicatedOutput;
    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private ID3D11Texture2D? _captureTexture;

    // If _captureTexture holds a frame: a new one is empty until the first frame is copied to it. Only used under _captureLock.
    private bool _captureTextureHasFrame;

    private readonly Dictionary<CaptureZone<ColorBGRA>, ZoneTextures> _textures = [];

    #endregion

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="DX11ScreenCapture"/> class.
    /// </summary>
    /// <param name="factory">The <see cref="IDXGIFactory1"/> used to create underlying objects.</param>
    /// <param name="display">The <see cref="Display"/> to duplicate.</param>
    /// <param name="options">The options of the capture.</param>
    internal DX11ScreenCapture(IDXGIFactory1 factory, Display display, DX11CaptureOptions options)
        : base(display)
    {
        this._factory = factory;
        this.Options = options;
        this._timeout = DX11CaptureOptions.ToMilliseconds(options.Timeout);

        Restart();
    }

    #endregion

    #region Methods

    /// <inheritdoc />
    protected override CaptureResult PerformScreenCapture(out long timestamp)
    {
        timestamp = 0;

        lock (_captureLock)
        {
            if ((_context == null) || (_duplicatedOutput == null) || (_captureTexture == null))
                return RestartCapture();

            try
            {
                return AcquireFrame(_duplicatedOutput, _context, _captureTexture, out timestamp);
            }
            catch (SharpGenException dxException) when ((dxException.ResultCode == ResultCode.AccessLost)
                                                      || (dxException.ResultCode == ResultCode.AccessDenied)
                                                      || (dxException.ResultCode == ResultCode.InvalidCall))
            {
                ReportError(dxException);

                // AcquireFrame released the frame (if it got one) before the restart disposes the duplication.
                return RestartCapture();
            }
        }
    }

    private CaptureResult AcquireFrame(IDXGIOutputDuplication duplicatedOutput, ID3D11DeviceContext context, ID3D11Texture2D captureTexture, out long timestamp)
    {
        timestamp = 0;

        IDXGIResource? screenResource = null;
        bool acquired = false;
        try
        {
            Result acquireResult = duplicatedOutput.AcquireNextFrame(_timeout, out OutduplFrameInfo frameInfo, out screenResource);
            if (acquireResult == ResultCode.WaitTimeout) 
                return CaptureResult.Timeout;

            acquireResult.CheckError();
            acquired = true;

            // No present since the last frame (e.g. only the mouse cursor moved): the image didn't change.
            if ((screenResource == null) || (frameInfo.LastPresentTime == 0)) return CaptureResult.NoChange;

            using ID3D11Texture2D screenTexture = screenResource.QueryInterface<ID3D11Texture2D>();
            context.CopySubresourceRegion(captureTexture, 0, 0, 0, 0, screenTexture, 0);
            _captureTextureHasFrame = true;

            timestamp = frameInfo.LastPresentTime;

            return CaptureResult.NewFrame;
        }
        finally
        {
            try
            {
                screenResource?.Dispose();
                if (acquired)
                    duplicatedOutput.ReleaseFrame();
            }
            catch (Exception ex) { ReportError(ex); }
        }
    }

    /// <inheritdoc />
    protected override bool PerformCaptureZoneUpdate(CaptureZone<ColorBGRA> captureZone, Span<byte> buffer)
    {
        ArgumentNullException.ThrowIfNull(captureZone);

        // _captureLock: another thread can capture (or restart) meanwhile, and both use the immediate context.
        lock (_captureLock)
            lock (_texturesLock)
            {
                // E.g. a Restart on another thread replaced the capture texture after this capture got its frame.
                if ((_device == null) || (_context == null) || (_captureTexture == null) || !_captureTextureHasFrame) return false;

                // Set by the restart under these locks, so it matches the capture texture.
                Display display = Display;
                if (!captureZone.Region.IsWithin(display.Width, display.Height)) return false;

                ZoneTextures? textures = GetTextures(captureZone, display);
                if (textures == null) return false;

                Box sourceBox = GetSourceBox(captureZone, display).ToBox();
                if (textures.ScalingTexture != null)
                {
                    _context.CopySubresourceRegion(textures.ScalingTexture, 0, 0, 0, 0, _captureTexture, 0, sourceBox);
                    _context.GenerateMips(textures.ScalingTextureView);
                    _context.CopySubresourceRegion(textures.StagingTexture, 0, 0, 0, 0, textures.ScalingTexture, textures.DownscaleLevel);
                }
                else
                    _context.CopySubresourceRegion(textures.StagingTexture, 0, 0, 0, 0, _captureTexture, 0, sourceBox);

                MappedSubresource mapSource = _context.Map(textures.StagingTexture, 0, MapMode.Read, MapFlags.None);
                try
                {
                    ReadOnlySpan<byte> source = mapSource.AsSpan(mapSource.RowPitch * textures.StagingHeight);
                    CopyRotateHelper.Copy(display.Rotation, source, mapSource.RowPitch, captureZone.Width, captureZone.Height, buffer);
                }
                finally
                {
                    _context.Unmap(textures.StagingTexture, 0);
                }
            }

        return true;
    }

    /// <summary>
    /// Gets the textures of a zone and (re)creates them if the zone is new or was resized.
    /// </summary>
    /// <remarks>
    /// Called under the zone lock (and <see cref="_captureLock"/> and <see cref="_texturesLock"/>), so the zone doesn't change meanwhile.
    /// Creating them here instead of on register/update means a capture never sees a zone without textures or with textures of the wrong size.
    /// </remarks>
    /// <returns>The textures, or <c>null</c> if the zone was unregistered.</returns>
    private ZoneTextures? GetTextures(CaptureZone<ColorBGRA> captureZone, Display display)
    {
        if (_textures.TryGetValue(captureZone, out ZoneTextures? textures))
        {
            if (textures.FitsSizeOf(captureZone)) return textures;

            textures.Dispose();
            _textures.Remove(captureZone);
        }

        // A running capture can still hold an unregistered zone in its snapshot. UnregisterCaptureZone disposes the textures after removing
        // the zone (under _texturesLock), so creating them only for registered zones means none are left behind.
        if (!CaptureZones.Contains(captureZone)) return null;

        textures = CreateTextures(captureZone, display);
        _textures.Add(captureZone, textures);

        return textures;
    }

    /// <inheritdoc />
    public override bool UnregisterCaptureZone(CaptureZone<ColorBGRA> captureZone)
    {
        if (!base.UnregisterCaptureZone(captureZone)) return false;

        lock (_texturesLock)
            if (_textures.Remove(captureZone, out ZoneTextures? textures))
                textures.Dispose();

        return true;
    }

    private static SourceBox GetSourceBox(CaptureZone<ColorBGRA> captureZone, Display display)
        => DX11ZoneLayout.GetSourceBox(display.Rotation, display.Width, display.Height,
                                       captureZone.Region.X, captureZone.Region.Y, captureZone.Region.Width, captureZone.Region.Height);

    private ZoneTextures CreateTextures(CaptureZone<ColorBGRA> captureZone, Display display)
    {
        // Textures only depend on the size, not on the position of the zone. A restart disposes them, so they're never left with another rotation.
        (int unscaledWidth, int unscaledHeight) = DX11ZoneLayout.GetTextureSize(display.Rotation, captureZone.Region.Width, captureZone.Region.Height);
        (int width, int height) = DX11ZoneLayout.GetTextureSize(display.Rotation, captureZone.Width, captureZone.Height);

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

        return new ZoneTextures(captureZone.Region.Width, captureZone.Region.Height, captureZone.DownscaleLevel, height,
                                stagingTexture, scalingTexture, scalingTextureView);
    }

    /// <inheritdoc />
    protected override bool PerformRestart()
    {
        lock (_captureLock)
            lock (_texturesLock)
            {
                // Dispose releases everything under these locks after setting the flag: nothing created here may outlive it.
                if (_isDisposed) return false;

                try
                {
                    foreach (ZoneTextures textures in _textures.Values)
                        textures.Dispose();
                    _textures.Clear();

                    DisposeDX();

                    // Found by its id and read again: after a mode change (resolution, rotation) the display has other values,
                    // and after displays were added or removed the indices can point to another adapter or output.
                    (IDXGIAdapter1 foundAdapter, IDXGIOutput foundOutput, Display display) = DX11Devices.FindDisplay(_factory, Display.Id)
                                                                                         ?? throw new InvalidOperationException($"The display '{Display.Id}' isn't connected.");
                    using IDXGIAdapter1 adapter = foundAdapter;
                    using IDXGIOutput dxgiOutput = foundOutput;

                    D3D11.D3D11CreateDevice(adapter, DriverType.Unknown, DeviceCreationFlags.None, FEATURE_LEVELS, out _device).CheckError();
                    _context = _device!.ImmediateContext;

                    using IDXGIOutput5 output = dxgiOutput.QueryInterface<IDXGIOutput5>();

                    int width = display.Width;
                    int height = display.Height;
                    if (display.Rotation is Rotation.Rotate90 or Rotation.Rotate270)
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

                    if (Options.UseDuplicateOutput1)
                        _duplicatedOutput = output.DuplicateOutput1(_device, [Format.B8G8R8A8_UNorm]); // DarthAffe 27.02.2021: This prepares for the use of 10bit color depth
                    else
                        _duplicatedOutput = output.DuplicateOutput(_device);

                    // Under _texturesLock: zone updates read the display there, so they never see it differ from the capture texture.
                    SetDisplay(display);

                    return true;
                }
                catch (Exception ex)
                {
                    // E.g. while the secure desktop (UAC, lock screen) is shown. The next capture tries again.
                    DisposeDX();
                    ReportError(ex);

                    return false;
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
            lock (_texturesLock)
            {
                foreach (ZoneTextures textures in _textures.Values)
                    try { textures.Dispose(); } catch { /**/ }
                _textures.Clear();

                DisposeDX();
            }
    }

    private void DisposeDX()
    {
        try { _duplicatedOutput?.Dispose(); } catch { /**/ }
        try { _captureTexture?.Dispose(); } catch { /**/ }
        try { _context?.Dispose(); } catch { /**/ }
        try { _device?.Dispose(); } catch { /**/ }

        _duplicatedOutput = null;
        _captureTexture = null;
        _captureTextureHasFrame = false;
        _context = null;
        _device = null;
    }

    #endregion

    private sealed class ZoneTextures : IDisposable
    {
        #region Properties & Fields

        // The zone size (display orientation) the textures were created for.
        public int UnscaledWidth { get; }
        public int UnscaledHeight { get; }
        public int DownscaleLevel { get; }

        /// <summary>
        /// The height of the staging texture (texture orientation).
        /// </summary>
        public int StagingHeight { get; }

        public ID3D11Texture2D StagingTexture { get; }
        public ID3D11Texture2D? ScalingTexture { get; }
        public ID3D11ShaderResourceView? ScalingTextureView { get; }

        #endregion

        #region Constructors

        public ZoneTextures(int unscaledWidth, int unscaledHeight, int downscaleLevel, int stagingHeight,
                            ID3D11Texture2D stagingTexture, ID3D11Texture2D? scalingTexture, ID3D11ShaderResourceView? scalingTextureView)
        {
            this.UnscaledWidth = unscaledWidth;
            this.UnscaledHeight = unscaledHeight;
            this.DownscaleLevel = downscaleLevel;
            this.StagingHeight = stagingHeight;
            this.StagingTexture = stagingTexture;
            this.ScalingTexture = scalingTexture;
            this.ScalingTextureView = scalingTextureView;
        }

        #endregion

        #region Methods

        /// <summary>
        /// Checks if the textures were created for the current size and downscale level of <paramref name="captureZone"/>.
        /// </summary>
        public bool FitsSizeOf(CaptureZone<ColorBGRA> captureZone)
            => (captureZone.Region.Width == UnscaledWidth) && (captureZone.Region.Height == UnscaledHeight) && (captureZone.DownscaleLevel == DownscaleLevel);

        public void Dispose()
        {
            StagingTexture.Dispose();
            ScalingTexture?.Dispose();
            ScalingTextureView?.Dispose();
        }

        #endregion
    }
}