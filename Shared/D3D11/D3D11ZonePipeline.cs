using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using HPPH;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using MapFlags = Vortice.Direct3D11.MapFlags;

namespace ScreenCapture.NET;

/// <summary>
/// Writes the zones of a Direct3D 11 capture from its frames: holds the current frame in a capture texture, crops and downscales the zones on the GPU
/// (through the mip chains of their scaling textures) and reads them back through their staging textures.
/// </summary>
/// <remarks>
/// The capture creates the device and gets the frames. It initializes the pipeline after every restart, hands it every new frame,
/// queues the zone copies after every capture that keeps a frame and lets the pipeline write the zones.
/// <para>
/// Every method except <see cref="RemoveZone"/> is called under the capture lock of the capture, which also guards the immediate context.
/// The zone textures are guarded by a lock of their own, acquired after the capture lock: <see cref="RemoveZone"/> takes only that one,
/// so unregistering a zone doesn't wait for a capture waiting for a frame.
/// </para>
/// </remarks>
internal sealed class D3D11ZonePipeline
{
    #region Properties & Fields

    private readonly ID3D11ZonePipelineHost _host;

    // Guards _textures. Acquired after the capture lock; RemoveZone takes only this one.
    private readonly Lock _texturesLock = new();
    private readonly Dictionary<CaptureZone<ColorBGRA>, ZoneTextures> _textures = [];

    // Owned by the capture, which disposes them after Release.
    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;

    private ID3D11Texture2D? _captureTexture;

    // The display shown by the capture texture, the rotation that turns the capture texture into its orientation,
    // and the display with that rotation (what GetSourceRegion expects).
    private Display _display;
    private ImageRotation _sourceRotation;
    private Display _sourceDisplay;

    // If _captureTexture holds a frame: a new one is empty until the first frame is copied to it.
    private bool _hasFrame;

    // Counts the frames copied to _captureTexture, so the staging texture of a zone knows if it holds the current one.
    private long _version;

    // Only for HDR frames: created on the first one, tone maps them into _captureTexture.
    private HdrFrame? _hdrFrame;

    /// <summary>
    /// Gets a value indicating whether the pipeline was initialized (and not released since).
    /// </summary>
    public bool IsInitialized => _captureTexture != null;

    /// <summary>
    /// Gets a value indicating whether the capture texture holds a frame the zones can be written from.
    /// </summary>
    public bool HasFrame => _hasFrame;

    #endregion

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="D3D11ZonePipeline"/> class.
    /// </summary>
    /// <param name="host">The capture whose zones are written.</param>
    public D3D11ZonePipeline(ID3D11ZonePipelineHost host)
    {
        this._host = host;
    }

    #endregion

    #region Methods

    /// <summary>
    /// Releases the textures of the previous device and creates the capture texture for the frames of <paramref name="display"/>.
    /// </summary>
    /// <param name="device">The device of the frames. It stays owned by the capture, which keeps it until <see cref="Release"/>.</param>
    /// <param name="context">The immediate context of <paramref name="device"/>, owned by the capture like the device.</param>
    /// <param name="display">The display the frames show.</param>
    /// <param name="sourceRotation">
    /// How the frames are rotated (clockwise) into the orientation of the display: <see cref="Display.Rotation"/> for frames in the native orientation of the output,
    /// <see cref="ImageRotation.None"/> for frames that already are in the orientation of the display.
    /// </param>
    public void Initialize(ID3D11Device device, ID3D11DeviceContext context, Display display, ImageRotation sourceRotation)
    {
        Release();

        (int width, int height) = PixelHelper.GetRotatedSize(display.Width, display.Height, sourceRotation);
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
        _captureTexture = device.CreateTexture2D(captureTextureDesc);

        _device = device;
        _context = context;
        _display = display;
        _sourceRotation = sourceRotation;
        _sourceDisplay = display with { Rotation = sourceRotation };
    }

    /// <summary>
    /// Disposes all textures (the ones of the zones included) and forgets the device. Errors while disposing are ignored.
    /// </summary>
    /// <remarks>
    /// Called before the capture disposes the device. Zone textures are created again for the next frame after <see cref="Initialize"/>.
    /// </remarks>
    public void Release()
    {
        lock (_texturesLock)
        {
            foreach (ZoneTextures textures in _textures.Values)
                try { textures.Dispose(); } catch { /**/ }
            _textures.Clear();
        }

        try { _hdrFrame?.Dispose(); } catch { /**/ }
        try { _captureTexture?.Dispose(); } catch { /**/ }

        _hdrFrame = null;
        _captureTexture = null;
        _hasFrame = false;
        _context = null;
        _device = null;
    }

    /// <summary>
    /// Copies a frame (<c>B8G8R8A8_UNorm</c>, the size of the capture texture) to the capture texture. Only queues the work on the GPU.
    /// </summary>
    public void CopyFrame(ID3D11Texture2D frame)
    {
        ID3D11DeviceContext context = _context ?? throw CreateNotInitializedException();

        context.CopySubresourceRegion(_captureTexture!, 0, 0, 0, 0, frame, 0);
        SetFrame();
    }

    /// <summary>
    /// Tone maps an HDR frame (scRGB, <c>R16G16B16A16_Float</c>, the size of the capture texture) into the capture texture. Only queues the work on the GPU.
    /// </summary>
    /// <param name="frame">The frame.</param>
    /// <param name="sdrWhiteLevel">The current SDR white level of the display in nits (see <see cref="HdrToneMapper.GetWhiteScale"/>); <c>null</c> keeps the last one.</param>
    public void ToneMapFrame(ID3D11Texture2D frame, float? sdrWhiteLevel)
    {
        ID3D11DeviceContext context = _context ?? throw CreateNotInitializedException();

        // The frame can't be bound to a shader (e.g. the one of a duplication): it's copied to a texture that can.
        _hdrFrame ??= HdrFrame.Create(_device!, _captureTexture!);
        context.CopySubresourceRegion(_hdrFrame.Texture, 0, 0, 0, 0, frame, 0);

        if (sdrWhiteLevel is { } whiteLevel)
            _hdrFrame.WhiteScale = HdrToneMapper.GetWhiteScale(whiteLevel);

        _hdrFrame.ToneMapper.Draw(context, _hdrFrame.TextureView, _hdrFrame.CaptureTextureTarget, _hdrFrame.Width, _hdrFrame.Height, _hdrFrame.WhiteScale);
        SetFrame();
    }

    private void SetFrame()
    {
        _hasFrame = true;
        _version++;
    }

    private static InvalidOperationException CreateNotInitializedException() => new("The zone pipeline isn't initialized.");

    /// <summary>
    /// Queues the copies of the current frame to the staging textures of all zones that are probably written by this capture, without waiting for them.
    /// </summary>
    /// <remarks>
    /// Called after a capture that keeps a frame, before the zones are written; does nothing without a frame. Mapping a staging texture waits for the GPU;
    /// with all copies queued first, the first map waits for all of them at once instead of every zone waiting for its own copy.
    /// <para>
    /// Without the zone locks: a zone can change until it's written. Its staging texture remembers what it holds, and the write copies again if that doesn't match.
    /// Errors are reported to the capture, the writes try again.
    /// </para>
    /// </remarks>
    public void QueueZoneCopies()
    {
        if (!_hasFrame || (_context == null) || (_captureTexture == null)) return;

        bool queued = false;

        try
        {
            lock (_texturesLock)
                foreach (CaptureZone<ColorBGRA> captureZone in _host.CaptureZones)
                {
                    // Zones that aren't written by this capture (not updated automatically, already showing this frame) skip the copy.
                    if (!captureZone.IsValid || !(captureZone.AutoUpdate || captureZone.IsUpdateRequested)) continue;

                    // Read separately, so the region and the level can belong to different changes: the size is computed from exactly these values.
                    CaptureRegion region = captureZone.Region;
                    if (!region.IsWithin(_display.Width, _display.Height)) continue;

                    (int width, int height, int downscaleLevel) = _host.CalculateScaledSize(region.Width, region.Height, captureZone.DownscaleLevel);

                    ZoneTextures? textures = GetTextures(captureZone, region, width, height, downscaleLevel);
                    if ((textures == null) || textures.HoldsCopy(_version, region)) continue;

                    CopyToStaging(_context, _captureTexture, textures, region);
                    queued = true;
                }
        }
        catch (Exception ex)
        {
            // E.g. no memory for new textures: the zone update tries again and reports its own error.
            _host.ReportError(ex);
        }

        // Hands the copies to the GPU now: it works on them while the capture raises its events.
        if (queued)
            _context.Flush();
    }

    /// <summary>
    /// Writes a zone from the current frame. Called under the zone lock (and the capture lock).
    /// </summary>
    /// <param name="captureZone">The zone.</param>
    /// <param name="buffer">The buffer of the zone (see <see cref="AbstractScreenCapture{TColor}.PerformCaptureZoneUpdate"/>).</param>
    /// <returns><c>true</c> if the zone was written; <c>false</c> if there's no frame, the zone doesn't lie within it or the zone was unregistered.</returns>
    public bool WriteZone(CaptureZone<ColorBGRA> captureZone, Span<ColorBGRA> buffer)
    {
        lock (_texturesLock)
        {
            // E.g. a Restart on another thread replaced the capture texture after this capture got its frame.
            if ((_context == null) || (_captureTexture == null) || !_hasFrame) return false;

            // The display of the capture texture: the zone's own one can still be the previous one.
            if (!captureZone.Region.IsWithin(_display.Width, _display.Height)) return false;

            // Under the zone lock these values belong together (and to the zone's buffer).
            CaptureRegion region = captureZone.Region;
            ZoneTextures? textures = GetTextures(captureZone, region, captureZone.Width, captureZone.Height, captureZone.DownscaleLevel);
            if (textures == null) return false;

            // Usually queued by QueueZoneCopies. Not if the zone changed since, or wasn't expected to be written (e.g. an update requested meanwhile).
            if (!textures.HoldsCopy(_version, region))
                CopyToStaging(_context, _captureTexture, textures, region);

            // Waits until the GPU did the copy. The copies of all zones were queued before and run meanwhile, so mostly only the first zone waits.
            MappedSubresource mapSource = _context.Map(textures.StagingTexture, 0, MapMode.Read, MapFlags.None);
            try
            {
                // The staging texture is the downscaled region in the orientation of the capture texture; only the rotation is left.
                ReadOnlySpan<byte> source = mapSource.AsSpan(mapSource.RowPitch * textures.StagingHeight);
                _host.WriteCaptureZoneFromScaled(RefImage<ColorBGRA>.Wrap(source, textures.StagingWidth, textures.StagingHeight, mapSource.RowPitch), captureZone, buffer, _sourceRotation);
            }
            finally
            {
                _context.Unmap(textures.StagingTexture, 0);
            }
        }

        return true;
    }

    /// <summary>
    /// Disposes the textures of an unregistered zone. Called after the zone was removed from <see cref="ID3D11ZonePipelineHost.CaptureZones"/>, without the capture lock.
    /// </summary>
    public void RemoveZone(CaptureZone<ColorBGRA> captureZone)
    {
        lock (_texturesLock)
            if (_textures.Remove(captureZone, out ZoneTextures? textures))
                textures.Dispose();
    }

    /// <summary>
    /// Copies the zone's region of the frame to its staging texture (downscaled through the mip chain of its scaling texture). Only queues the work on the GPU.
    /// </summary>
    private void CopyToStaging(ID3D11DeviceContext context, ID3D11Texture2D captureTexture, ZoneTextures textures, CaptureRegion region)
    {
        CaptureRegion source = _host.GetSourceRegion(region, _sourceDisplay);
        Box sourceBox = new(source.X, source.Y, 0, source.X + source.Width, source.Y + source.Height, 1);
        if (textures.ScalingTexture != null)
        {
            context.CopySubresourceRegion(textures.ScalingTexture, 0, 0, 0, 0, captureTexture, 0, sourceBox);
            context.GenerateMips(textures.ScalingTextureView);
            context.CopySubresourceRegion(textures.StagingTexture, 0, 0, 0, 0, textures.ScalingTexture, textures.DownscaleLevel);
        }
        else
            context.CopySubresourceRegion(textures.StagingTexture, 0, 0, 0, 0, captureTexture, 0, sourceBox);

        textures.SetCopy(_version, region);
    }

    /// <summary>
    /// Gets the textures of a zone and (re)creates them if the zone is new or was resized.
    /// </summary>
    /// <remarks>
    /// Called under the capture lock and <see cref="_texturesLock"/>, with the size the zone had when it was read (the zone lock isn't held while copies are queued).
    /// Creating them here instead of on register/update means a capture never sees a zone without textures or with textures of the wrong size.
    /// </remarks>
    /// <param name="captureZone">The zone.</param>
    /// <param name="region">The region of the zone.</param>
    /// <param name="width">The width of the zone's pixels (after downscaling) for <paramref name="region"/> and <paramref name="downscaleLevel"/>.</param>
    /// <param name="height">The height of the zone's pixels (after downscaling) for <paramref name="region"/> and <paramref name="downscaleLevel"/>.</param>
    /// <param name="downscaleLevel">The downscale level of the zone.</param>
    /// <returns>The textures, or <c>null</c> if the zone was unregistered.</returns>
    private ZoneTextures? GetTextures(CaptureZone<ColorBGRA> captureZone, CaptureRegion region, int width, int height, int downscaleLevel)
    {
        if (_textures.TryGetValue(captureZone, out ZoneTextures? textures))
        {
            if (textures.Fits(region.Width, region.Height, downscaleLevel)) return textures;

            textures.Dispose();
            _textures.Remove(captureZone);
        }

        // A running capture can still hold an unregistered zone in its snapshot. Unregistering disposes the textures after removing
        // the zone (RemoveZone, under _texturesLock), so creating them only for registered zones means none are left behind.
        if (!_host.CaptureZones.Contains(captureZone)) return null;

        textures = CreateTextures(region, width, height, downscaleLevel);
        _textures.Add(captureZone, textures);

        return textures;
    }

    private ZoneTextures CreateTextures(CaptureRegion region, int scaledWidth, int scaledHeight, int downscaleLevel)
    {
        // Textures only depend on the size, not on the position of the zone. Initialize releases them, so they're never left with another rotation.
        (int unscaledWidth, int unscaledHeight) = PixelHelper.GetRotatedSize(region.Width, region.Height, _sourceRotation);
        (int width, int height) = PixelHelper.GetRotatedSize(scaledWidth, scaledHeight, _sourceRotation);

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
        if (downscaleLevel > 0)
        {
            Texture2DDescription scalingTextureDesc = new()
            {
                CPUAccessFlags = CpuAccessFlags.None,
                BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
                Format = Format.B8G8R8A8_UNorm,
                Width = unscaledWidth,
                Height = unscaledHeight,
                MiscFlags = ResourceOptionFlags.GenerateMips,
                MipLevels = downscaleLevel + 1,
                ArraySize = 1,
                SampleDescription = { Count = 1, Quality = 0 },
                Usage = ResourceUsage.Default
            };
            scalingTexture = _device.CreateTexture2D(scalingTextureDesc);
            scalingTextureView = _device.CreateShaderResourceView(scalingTexture);
        }

        return new ZoneTextures(region.Width, region.Height, downscaleLevel, width, height, stagingTexture, scalingTexture, scalingTextureView);
    }

    #endregion

    /// <summary>
    /// The resources tone mapping HDR frames into the capture texture.
    /// </summary>
    private sealed class HdrFrame : IDisposable
    {
        #region Properties & Fields

        public int Width { get; }
        public int Height { get; }

        // The HDR frame, readable by the shader.
        public ID3D11Texture2D Texture { get; }
        public ID3D11ShaderResourceView TextureView { get; }

        public ID3D11RenderTargetView CaptureTextureTarget { get; }
        public HdrToneMapper ToneMapper { get; }

        // The last SDR white level read (see HdrToneMapper.GetWhiteScale); no scaling until one was read.
        public float WhiteScale { get; set; } = 1;

        #endregion

        #region Constructors

        private HdrFrame(int width, int height, ID3D11Texture2D texture, ID3D11ShaderResourceView textureView, ID3D11RenderTargetView captureTextureTarget, HdrToneMapper toneMapper)
        {
            this.Width = width;
            this.Height = height;
            this.Texture = texture;
            this.TextureView = textureView;
            this.CaptureTextureTarget = captureTextureTarget;
            this.ToneMapper = toneMapper;
        }

        #endregion

        #region Methods

        public static HdrFrame Create(ID3D11Device device, ID3D11Texture2D captureTexture)
        {
            Texture2DDescription captureTextureDesc = captureTexture.Description;

            ID3D11Texture2D? texture = null;
            ID3D11ShaderResourceView? textureView = null;
            ID3D11RenderTargetView? captureTextureTarget = null;
            try
            {
                Texture2DDescription textureDesc = new()
                {
                    CPUAccessFlags = CpuAccessFlags.None,
                    BindFlags = BindFlags.ShaderResource,
                    Format = Format.R16G16B16A16_Float,
                    Width = captureTextureDesc.Width,
                    Height = captureTextureDesc.Height,
                    MiscFlags = ResourceOptionFlags.None,
                    MipLevels = 1,
                    ArraySize = 1,
                    SampleDescription = { Count = 1, Quality = 0 },
                    Usage = ResourceUsage.Default
                };
                texture = device.CreateTexture2D(textureDesc);
                textureView = device.CreateShaderResourceView(texture);
                captureTextureTarget = device.CreateRenderTargetView(captureTexture);

                return new HdrFrame(captureTextureDesc.Width, captureTextureDesc.Height, texture, textureView, captureTextureTarget, new HdrToneMapper(device));
            }
            catch
            {
                captureTextureTarget?.Dispose();
                textureView?.Dispose();
                texture?.Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            ToneMapper.Dispose();
            CaptureTextureTarget.Dispose();
            TextureView.Dispose();
            Texture.Dispose();
        }

        #endregion
    }

    private sealed class ZoneTextures : IDisposable
    {
        #region Properties & Fields

        // The zone size (display orientation) the textures were created for.
        public int UnscaledWidth { get; }
        public int UnscaledHeight { get; }
        public int DownscaleLevel { get; }

        // The size of the staging texture (orientation of the capture texture).
        public int StagingWidth { get; }
        public int StagingHeight { get; }

        public ID3D11Texture2D StagingTexture { get; }
        public ID3D11Texture2D? ScalingTexture { get; }
        public ID3D11ShaderResourceView? ScalingTextureView { get; }

        // What the staging texture holds: the region of the capture texture's frame with this version (0: nothing yet).
        private long _copiedVersion;
        private CaptureRegion _copiedRegion;

        #endregion

        #region Constructors

        public ZoneTextures(int unscaledWidth, int unscaledHeight, int downscaleLevel, int stagingWidth, int stagingHeight,
                            ID3D11Texture2D stagingTexture, ID3D11Texture2D? scalingTexture, ID3D11ShaderResourceView? scalingTextureView)
        {
            this.UnscaledWidth = unscaledWidth;
            this.UnscaledHeight = unscaledHeight;
            this.DownscaleLevel = downscaleLevel;
            this.StagingWidth = stagingWidth;
            this.StagingHeight = stagingHeight;
            this.StagingTexture = stagingTexture;
            this.ScalingTexture = scalingTexture;
            this.ScalingTextureView = scalingTextureView;
        }

        #endregion

        #region Methods

        /// <summary>
        /// Checks if the textures were created for a zone of the given size and downscale level.
        /// </summary>
        public bool Fits(int unscaledWidth, int unscaledHeight, int downscaleLevel)
            => (unscaledWidth == UnscaledWidth) && (unscaledHeight == UnscaledHeight) && (downscaleLevel == DownscaleLevel);

        /// <summary>
        /// Checks if the staging texture holds (or the GPU is writing) <paramref name="region"/> of the frame with the given version. Called under the capture lock.
        /// </summary>
        public bool HoldsCopy(long version, CaptureRegion region) => (version == _copiedVersion) && (region == _copiedRegion);

        /// <summary>
        /// Records that a copy of <paramref name="region"/> of the frame with the given version to the staging texture was queued. Called under the capture lock.
        /// </summary>
        public void SetCopy(long version, CaptureRegion region)
        {
            _copiedVersion = version;
            _copiedRegion = region;
        }

        public void Dispose()
        {
            StagingTexture.Dispose();
            ScalingTexture?.Dispose();
            ScalingTextureView?.Dispose();
        }

        #endregion
    }
}
