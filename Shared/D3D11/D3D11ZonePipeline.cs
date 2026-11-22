using System;
using System.Collections.Generic;
using System.Threading;
using HPPH;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using MapFlags = Vortice.Direct3D11.MapFlags;

namespace ScreenCapture.NET;

/// <summary>
/// Writes the zones of a Direct3D 11 capture: holds the frame in a capture texture, crops and downscales the zones on the GPU and reads them back through staging textures.
/// </summary>
/// <remarks>
/// The capture owns the device, initializes the pipeline after every restart and hands it the frames.
/// <para>
/// Everything except <see cref="RemoveZone"/> is called under the capture lock, which also guards the immediate context.
/// The zone textures have their own lock, taken after the capture lock. <see cref="RemoveZone"/> takes only that one, so it doesn't wait for a capture waiting for a frame.
/// </para>
/// </remarks>
internal sealed class D3D11ZonePipeline
{
    #region Properties & Fields

    private readonly Lock _texturesLock = new();
    private readonly Dictionary<CaptureZone<ColorBGRA>, ZoneTextures> _textures = [];

    // Owned by the capture.
    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;

    private ID3D11Texture2D? _captureTexture;
    private Box _captureTextureBox;

    // The display of the capture texture and how the texture is rotated into its orientation.
    private Display _display;
    private ImageRotation _sourceRotation;

    private bool _hasFrame;

    // Counts the frames copied to _captureTexture, so a staging texture knows if it holds the current one.
    private long _version;

    // Created with the first HDR frame.
    private HdrFrame? _hdrFrame;

    /// <summary>
    /// Gets a value indicating whether the pipeline was initialized.
    /// </summary>
    public bool IsInitialized => _captureTexture != null;

    /// <summary>
    /// Gets a value indicating whether the capture texture holds a frame the zones can be written from.
    /// </summary>
    public bool HasFrame => _hasFrame;

    #endregion

    #region Methods

    /// <summary>
    /// Releases the textures of the previous device and creates the capture texture for the frames of <paramref name="display"/>.
    /// </summary>
    /// <param name="device">The device of the frames, owned by the capture.</param>
    /// <param name="context">The immediate context of <paramref name="device"/>.</param>
    /// <param name="display">The display the frames show.</param>
    /// <param name="sourceRotation">How the frames are rotated (clockwise) into the orientation of the display (see <see cref="CaptureZoneWriter.Write{TColor}"/>).</param>
    public void Initialize(ID3D11Device device, ID3D11DeviceContext context, Display display, ImageRotation sourceRotation)
    {
        Release();

        (int width, int height) = PixelHelper.GetRotatedSize(display.Width, display.Height, sourceRotation);
        Texture2DDescription captureTextureDesc = new()
        {
            CPUAccessFlags = CpuAccessFlags.None,
            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
            Format = Format.B8G8R8A8_UNorm,
            Width = (uint)width,
            Height = (uint)height,
            MiscFlags = ResourceOptionFlags.None,
            MipLevels = 1,
            ArraySize = 1,
            SampleDescription = { Count = 1, Quality = 0 },
            Usage = ResourceUsage.Default
        };
        _captureTexture = device.CreateTexture2D(captureTextureDesc);
        _captureTextureBox = new Box(0, 0, 0, width, height, 1);

        _device = device;
        _context = context;
        _display = display;
        _sourceRotation = sourceRotation;
    }

    /// <summary>
    /// Disposes all textures and forgets the device. Called before the capture disposes the device.
    /// </summary>
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
    /// Takes a frame into the capture texture: tone maps HDR frames, copies all others. Only queues the work on the GPU.
    /// </summary>
    /// <param name="frame">The frame, at least the size of the capture texture. Its top left part is used.</param>
    /// <param name="monitorTarget">The monitor, to read its SDR white level for HDR frames; <c>null</c> keeps the last one.</param>
    public void SetFrame(ID3D11Texture2D frame, WindowsDisplayDevices.MonitorTarget? monitorTarget)
    {
        if (frame.Description.Format == Format.R16G16B16A16_Float)
        {
            // Read on every frame (a few µs): the SDR brightness can change at any time.
            float? sdrWhiteLevel = (monitorTarget is { } target) && target.TryGetSdrWhiteLevel(out float whiteLevel) ? whiteLevel : null;
            ToneMapFrame(frame, sdrWhiteLevel);
        }
        else
            CopyFrame(frame);
    }

    /// <summary>
    /// Copies a frame (<c>B8G8R8A8_UNorm</c>) to the capture texture. Only queues the work on the GPU.
    /// </summary>
    /// <param name="frame">The frame, at least the size of the capture texture. Its top left part is used.</param>
    public void CopyFrame(ID3D11Texture2D frame)
    {
        ID3D11DeviceContext context = _context ?? throw CreateNotInitializedException();

        context.CopySubresourceRegion(_captureTexture!, 0, 0, 0, 0, frame, 0, _captureTextureBox);
        FrameCopied();
    }

    /// <summary>
    /// Tone maps an HDR frame (scRGB, <c>R16G16B16A16_Float</c>) into the capture texture. Only queues the work on the GPU.
    /// </summary>
    /// <param name="frame">The frame, at least the size of the capture texture. Its top left part is used.</param>
    /// <param name="sdrWhiteLevel">The SDR white level of the display in nits; <c>null</c> keeps the last one.</param>
    public void ToneMapFrame(ID3D11Texture2D frame, float? sdrWhiteLevel)
    {
        ID3D11DeviceContext context = _context ?? throw CreateNotInitializedException();

        // Frames (e.g. of a duplication) can't be bound to a shader.
        _hdrFrame ??= HdrFrame.Create(_device!, _captureTexture!);
        context.CopySubresourceRegion(_hdrFrame.Texture, 0, 0, 0, 0, frame, 0, _captureTextureBox);

        if (sdrWhiteLevel is { } whiteLevel)
            _hdrFrame.WhiteScale = HdrToneMapper.GetWhiteScale(whiteLevel);

        _hdrFrame.ToneMapper.Draw(context, _hdrFrame.TextureView, _hdrFrame.CaptureTextureTarget, _hdrFrame.Width, _hdrFrame.Height, _hdrFrame.WhiteScale);
        FrameCopied();
    }

    private void FrameCopied()
    {
        _hasFrame = true;
        _version++;
    }

    private static InvalidOperationException CreateNotInitializedException() => new("The zone pipeline isn't initialized.");

    /// <summary>
    /// Queues the copies of the current frame to the staging textures of all zones this capture will probably write.
    /// </summary>
    /// <remarks>
    /// With all copies queued first, only the first map waits for the GPU instead of every zone.
    /// <para>
    /// Called without the zone locks, so a zone can still change. The write copies again if the staging texture doesn't match.
    /// New and resized zones get their textures and copy when they're written.
    /// </para>
    /// </remarks>
    /// <param name="captureZones">The zones of the capture.</param>
    public void QueueZoneCopies(IReadOnlyList<CaptureZone<ColorBGRA>> captureZones)
    {
        if (!_hasFrame || (_context == null) || (_captureTexture == null)) return;

        bool queued = false;

        lock (_texturesLock)
            foreach (CaptureZone<ColorBGRA> captureZone in captureZones)
            {
                if (!captureZone.IsValid || !(captureZone.AutoUpdate || captureZone.IsUpdateRequested)) continue;

                // Region and level can belong to different changes. Then the textures don't fit or the write copies again.
                CaptureRegion region = captureZone.Region;
                if (!region.IsWithin(_display.Width, _display.Height)) continue;

                if (!_textures.TryGetValue(captureZone, out ZoneTextures? textures)
                 || !textures.Fits(region.Width, region.Height, captureZone.DownscaleLevel)
                 || textures.HoldsCopy(_version, region)) continue;

                CopyToStaging(_context, _captureTexture, textures, region);
                queued = true;
            }

        // The GPU works on them while the capture raises its events.
        if (queued)
            _context.Flush();
    }

    /// <summary>
    /// Writes a zone from the current frame. Called under the zone lock (and the capture lock), only for registered zones.
    /// </summary>
    /// <param name="captureZone">The zone.</param>
    /// <param name="buffer">The buffer of the zone.</param>
    /// <returns><c>true</c> if the zone was written; <c>false</c> if there's no frame or the zone doesn't lie within it.</returns>
    public bool WriteZone(CaptureZone<ColorBGRA> captureZone, Span<ColorBGRA> buffer)
    {
        lock (_texturesLock)
        {
            // A restart on another thread can have replaced the capture texture.
            if ((_context == null) || (_captureTexture == null) || !_hasFrame) return false;

            // The zone's own display can still be the previous one.
            if (!captureZone.Region.IsWithin(_display.Width, _display.Height)) return false;

            CaptureRegion region = captureZone.Region;
            ZoneTextures textures = GetTextures(captureZone);

            // Usually queued by QueueZoneCopies already.
            if (!textures.HoldsCopy(_version, region))
                CopyToStaging(_context, _captureTexture, textures, region);

            // Waits for the GPU.
            MappedSubresource mapSource = _context.Map(textures.StagingTexture, 0, MapMode.Read, MapFlags.None);
            try
            {
                // Already cropped and downscaled, only the rotation is left.
                ReadOnlySpan<byte> source = mapSource.AsSpan((int)mapSource.RowPitch * textures.StagingHeight);
                CaptureZoneWriter.WriteFromScaled(RefImage<ColorBGRA>.Wrap(source, textures.StagingWidth, textures.StagingHeight, (int)mapSource.RowPitch), captureZone, buffer, _sourceRotation);
            }
            finally
            {
                _context.Unmap(textures.StagingTexture, 0);
            }
        }

        return true;
    }

    /// <summary>
    /// Disposes the textures of an unregistered zone. Called without the capture lock.
    /// </summary>
    public void RemoveZone(CaptureZone<ColorBGRA> captureZone)
    {
        lock (_texturesLock)
            if (_textures.Remove(captureZone, out ZoneTextures? textures))
                textures.Dispose();
    }

    /// <summary>
    /// Copies the zone's region to its staging texture, downscaled through the mip chain of its scaling texture. Only queues the work on the GPU.
    /// </summary>
    private void CopyToStaging(ID3D11DeviceContext context, ID3D11Texture2D captureTexture, ZoneTextures textures, CaptureRegion region)
    {
        CaptureRegion source = CaptureZoneWriter.GetSourceRegion(region, _display.Width, _display.Height, _sourceRotation);
        Box sourceBox = new(source.X, source.Y, 0, source.X + source.Width, source.Y + source.Height, 1);
        if (textures.ScalingTexture != null)
        {
            context.CopySubresourceRegion(textures.ScalingTexture, 0, 0, 0, 0, captureTexture, 0, sourceBox);
            context.GenerateMips(textures.ScalingTextureView);
            context.CopySubresourceRegion(textures.StagingTexture, 0, 0, 0, 0, textures.ScalingTexture, (uint)textures.DownscaleLevel);
        }
        else
            context.CopySubresourceRegion(textures.StagingTexture, 0, 0, 0, 0, captureTexture, 0, sourceBox);

        textures.SetCopy(_version, region);
    }

    /// <summary>
    /// Gets the textures of a zone and (re)creates them if the zone is new or was resized.
    /// </summary>
    /// <remarks>
    /// Called under the zone lock, so region and size of the zone belong together.
    /// Created here instead of on register or update, so a capture never sees textures of the wrong size.
    /// </remarks>
    /// <param name="captureZone">The zone.</param>
    /// <returns>The textures.</returns>
    private ZoneTextures GetTextures(CaptureZone<ColorBGRA> captureZone)
    {
        CaptureRegion region = captureZone.Region;
        int downscaleLevel = captureZone.DownscaleLevel;

        if (_textures.TryGetValue(captureZone, out ZoneTextures? textures))
        {
            if (textures.Fits(region.Width, region.Height, downscaleLevel)) return textures;

            textures.Dispose();
            _textures.Remove(captureZone);
        }

        textures = CreateTextures(region, captureZone.Width, captureZone.Height, downscaleLevel);
        _textures.Add(captureZone, textures);

        return textures;
    }

    private ZoneTextures CreateTextures(CaptureRegion region, int scaledWidth, int scaledHeight, int downscaleLevel)
    {
        // Only the size matters, not the position. Initialize releases them, so the rotation always matches.
        (int unscaledWidth, int unscaledHeight) = PixelHelper.GetRotatedSize(region.Width, region.Height, _sourceRotation);
        (int width, int height) = PixelHelper.GetRotatedSize(scaledWidth, scaledHeight, _sourceRotation);

        Texture2DDescription stagingTextureDesc = new()
        {
            CPUAccessFlags = CpuAccessFlags.Read,
            BindFlags = BindFlags.None,
            Format = Format.B8G8R8A8_UNorm,
            Width = (uint)width,
            Height = (uint)height,
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
                Width = (uint)unscaledWidth,
                Height = (uint)unscaledHeight,
                MiscFlags = ResourceOptionFlags.GenerateMips,
                MipLevels = (uint)(downscaleLevel + 1),
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

        // No scaling until a white level was read.
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

                return new HdrFrame((int)captureTextureDesc.Width, (int)captureTextureDesc.Height, texture, textureView, captureTextureTarget, new HdrToneMapper(device));
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

        // In the orientation of the display.
        public int UnscaledWidth { get; }
        public int UnscaledHeight { get; }
        public int DownscaleLevel { get; }

        // In the orientation of the capture texture.
        public int StagingWidth { get; }
        public int StagingHeight { get; }

        public ID3D11Texture2D StagingTexture { get; }
        public ID3D11Texture2D? ScalingTexture { get; }
        public ID3D11ShaderResourceView? ScalingTextureView { get; }

        // What the staging texture holds (version 0: nothing).
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

        public bool Fits(int unscaledWidth, int unscaledHeight, int downscaleLevel)
            => (unscaledWidth == UnscaledWidth) && (unscaledHeight == UnscaledHeight) && (downscaleLevel == DownscaleLevel);

        // Also true while the GPU is still copying.
        public bool HoldsCopy(long version, CaptureRegion region) => (version == _copiedVersion) && (region == _copiedRegion);

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
