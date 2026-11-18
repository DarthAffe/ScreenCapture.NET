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

    // Counts the frames copied to _captureTexture, so the staging texture of a zone knows if it holds the current one. Only used under _captureLock.
    private long _captureTextureVersion;

    private readonly Dictionary<CaptureZone<ColorBGRA>, ZoneTextures> _textures = [];

    // The mouse pointer (only with DrawCursor), from the frames of the duplication; drawn into the zones when they're written.
    // _cursorX/_cursorY: the top left of the shape on the display. Only used under _captureLock.
    private CursorShape? _cursorShape;
    private bool _cursorVisible;
    private int _cursorX;
    private int _cursorY;
    private byte[] _cursorShapeBuffer = [];

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
                CaptureResult result = AcquireFrame(_duplicatedOutput, _context, _captureTexture, out timestamp);
                if (_captureTextureHasFrame && result is CaptureResult.NewFrame or CaptureResult.NoChange or CaptureResult.Timeout)
                    QueueZoneCopies(_context, _captureTexture);

                return result;
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

            // Read before the frame is released: the shape is only available until then.
            bool cursorChanged = Options.DrawCursor && UpdateCursor(duplicatedOutput, frameInfo);

            // No present since the last frame (e.g. only the mouse cursor moved): the image didn't change.
            if ((screenResource == null) || (frameInfo.LastPresentTime == 0))
            {
                // A pointer drawn into the zones changed: they're written again from the last frame, with the pointer as it is now.
                if (cursorChanged && _captureTextureHasFrame)
                {
                    timestamp = frameInfo.LastMouseUpdateTime;
                    return CaptureResult.NewFrame;
                }

                return CaptureResult.NoChange;
            }

            using ID3D11Texture2D screenTexture = screenResource.QueryInterface<ID3D11Texture2D>();
            context.CopySubresourceRegion(captureTexture, 0, 0, 0, 0, screenTexture, 0);
            _captureTextureHasFrame = true;
            _captureTextureVersion++;

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

    /// <summary>
    /// Takes the position, visibility and (if it changed) the shape of the mouse pointer from an acquired frame. Called under <see cref="_captureLock"/>.
    /// </summary>
    /// <returns><c>true</c> if the pointer drawn into the zones changed (moved, changed its shape, appeared or disappeared).</returns>
    private bool UpdateCursor(IDXGIOutputDuplication duplicatedOutput, in OutduplFrameInfo frameInfo)
    {
        // Neither the position nor the shape changed since the last frame (the position is always supplied with an update).
        if (frameInfo.LastMouseUpdateTime == 0) return false;

        bool wasDrawn = _cursorVisible && (_cursorShape != null);
        bool changed = false;

        if (frameInfo.PointerShapeBufferSize > 0)
        {
            _cursorShape = ReadCursorShape(duplicatedOutput, frameInfo.PointerShapeBufferSize);
            changed = true;
        }

        bool visible = frameInfo.PointerPosition.Visible;
        int x = frameInfo.PointerPosition.Position.X;
        int y = frameInfo.PointerPosition.Position.Y;
        changed |= (visible != _cursorVisible) || (x != _cursorX) || (y != _cursorY);

        _cursorVisible = visible;
        _cursorX = x;
        _cursorY = y;

        bool isDrawn = _cursorVisible && (_cursorShape != null);
        return changed && (wasDrawn || isDrawn);
    }

    private unsafe CursorShape? ReadCursorShape(IDXGIOutputDuplication duplicatedOutput, int size)
    {
        if (_cursorShapeBuffer.Length < size)
            _cursorShapeBuffer = new byte[size];

        int requiredSize;
        OutduplPointerShapeInfo shapeInfo;
        fixed (byte* buffer = _cursorShapeBuffer)
            duplicatedOutput.GetFramePointerShape(_cursorShapeBuffer.Length, (IntPtr)buffer, out requiredSize, out shapeInfo).CheckError();

        try
        {
            return CursorShape.Create((PointerShapeType)shapeInfo.Type, shapeInfo.Width, shapeInfo.Height, shapeInfo.Pitch, _cursorShapeBuffer.AsSpan(0, Math.Min(requiredSize, _cursorShapeBuffer.Length)));
        }
        catch (ArgumentException ex)
        {
            // A shape this can't draw (e.g. an unknown type): the pointer isn't drawn until the next shape.
            ReportError(ex);
            return null;
        }
    }

    /// <inheritdoc />
    protected override bool PerformCaptureZoneUpdate(CaptureZone<ColorBGRA> captureZone, Span<ColorBGRA> buffer)
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

                // Under the zone lock these values belong together (and to the zone's buffer).
                CaptureRegion region = captureZone.Region;
                ZoneTextures? textures = GetTextures(captureZone, display, region, captureZone.Width, captureZone.Height, captureZone.DownscaleLevel);
                if (textures == null) return false;

                // Usually queued by this capture's QueueZoneCopies. Not if the zone changed since, or wasn't expected to be written (e.g. an update requested meanwhile).
                if (!textures.HoldsCopy(_captureTextureVersion, region))
                    CopyToStaging(_context, _captureTexture, textures, region, display);

                // Waits until the GPU did the copy. The copies of all zones were queued before and run meanwhile, so mostly only the first zone waits.
                MappedSubresource mapSource = _context.Map(textures.StagingTexture, 0, MapMode.Read, MapFlags.None);
                try
                {
                    // The staging texture is the downscaled region in the native orientation of the output; only the rotation is left.
                    ReadOnlySpan<byte> source = mapSource.AsSpan(mapSource.RowPitch * textures.StagingHeight);
                    WriteCaptureZoneFromScaled(RefImage<ColorBGRA>.Wrap(source, textures.StagingWidth, textures.StagingHeight, mapSource.RowPitch), captureZone, buffer, display.Rotation);
                }
                finally
                {
                    _context.Unmap(textures.StagingTexture, 0);
                }

                // Into the zone's pixels, not the staging texture: that keeps the frame for writes with the pointer somewhere else.
                if (_cursorVisible && (_cursorShape != null))
                    _cursorShape.Draw(buffer, region, captureZone.Width, captureZone.Height, textures.DownscaleLevel, _cursorX, _cursorY);
            }

        return true;
    }

    /// <summary>
    /// Queues the copies of the current frame to the staging textures of all zones that are probably written by this capture, without waiting for them.
    /// </summary>
    /// <remarks>
    /// Called under <see cref="_captureLock"/> after a frame was acquired, before the zones are written. Mapping a staging texture waits for the GPU;
    /// with all copies queued first, the first map waits for all of them at once instead of every zone waiting for its own copy.
    /// <para>
    /// Without the zone locks: a zone can change until it's written. Its staging texture remembers what it holds, and the write copies again if that doesn't match.
    /// </para>
    /// </remarks>
    private void QueueZoneCopies(ID3D11DeviceContext context, ID3D11Texture2D captureTexture)
    {
        // Set by the restart under _captureLock, so it matches the capture texture.
        Display display = Display;
        bool queued = false;

        try
        {
            lock (_texturesLock)
                foreach (CaptureZone<ColorBGRA> captureZone in CaptureZones)
                {
                    // Zones that aren't written by this capture (not updated automatically, already showing this frame) skip the copy.
                    if (!captureZone.IsValid || !(captureZone.AutoUpdate || captureZone.IsUpdateRequested)) continue;

                    // Read separately, so the region and the level can belong to different changes: the size is computed from exactly these values.
                    CaptureRegion region = captureZone.Region;
                    if (!region.IsWithin(display.Width, display.Height)) continue;

                    (int width, int height, int downscaleLevel) = CalculateScaledSize(region.Width, region.Height, captureZone.DownscaleLevel);

                    ZoneTextures? textures = GetTextures(captureZone, display, region, width, height, downscaleLevel);
                    if ((textures == null) || textures.HoldsCopy(_captureTextureVersion, region)) continue;

                    CopyToStaging(context, captureTexture, textures, region, display);
                    queued = true;
                }
        }
        catch (Exception ex)
        {
            // E.g. no memory for new textures: the zone update tries again and reports its own error.
            ReportError(ex);
        }

        // Hands the copies to the GPU now: it works on them while the capture raises its events.
        if (queued)
            context.Flush();
    }

    /// <summary>
    /// Copies the zone's region of the frame to its staging texture (downscaled through the mip chain of its scaling texture). Only queues the work on the GPU.
    /// </summary>
    private void CopyToStaging(ID3D11DeviceContext context, ID3D11Texture2D captureTexture, ZoneTextures textures, CaptureRegion region, Display display)
    {
        CaptureRegion source = GetSourceRegion(region, display);
        Box sourceBox = new(source.X, source.Y, 0, source.X + source.Width, source.Y + source.Height, 1);
        if (textures.ScalingTexture != null)
        {
            context.CopySubresourceRegion(textures.ScalingTexture, 0, 0, 0, 0, captureTexture, 0, sourceBox);
            context.GenerateMips(textures.ScalingTextureView);
            context.CopySubresourceRegion(textures.StagingTexture, 0, 0, 0, 0, textures.ScalingTexture, textures.DownscaleLevel);
        }
        else
            context.CopySubresourceRegion(textures.StagingTexture, 0, 0, 0, 0, captureTexture, 0, sourceBox);

        textures.SetCopy(_captureTextureVersion, region);
    }

    /// <summary>
    /// Gets the textures of a zone and (re)creates them if the zone is new or was resized.
    /// </summary>
    /// <remarks>
    /// Called under <see cref="_captureLock"/> and <see cref="_texturesLock"/>, with the size the zone had when it was read (the zone lock isn't held while copies are queued).
    /// Creating them here instead of on register/update means a capture never sees a zone without textures or with textures of the wrong size.
    /// </remarks>
    /// <param name="captureZone">The zone.</param>
    /// <param name="display">The display of the capture texture.</param>
    /// <param name="region">The region of the zone.</param>
    /// <param name="width">The width of the zone's pixels (after downscaling) for <paramref name="region"/> and <paramref name="downscaleLevel"/>.</param>
    /// <param name="height">The height of the zone's pixels (after downscaling) for <paramref name="region"/> and <paramref name="downscaleLevel"/>.</param>
    /// <param name="downscaleLevel">The downscale level of the zone.</param>
    /// <returns>The textures, or <c>null</c> if the zone was unregistered.</returns>
    private ZoneTextures? GetTextures(CaptureZone<ColorBGRA> captureZone, Display display, CaptureRegion region, int width, int height, int downscaleLevel)
    {
        if (_textures.TryGetValue(captureZone, out ZoneTextures? textures))
        {
            if (textures.Fits(region.Width, region.Height, downscaleLevel)) return textures;

            textures.Dispose();
            _textures.Remove(captureZone);
        }

        // A running capture can still hold an unregistered zone in its snapshot. UnregisterCaptureZone disposes the textures after removing
        // the zone (under _texturesLock), so creating them only for registered zones means none are left behind.
        if (!CaptureZones.Contains(captureZone)) return null;

        textures = CreateTextures(display, region, width, height, downscaleLevel);
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

    private ZoneTextures CreateTextures(Display display, CaptureRegion region, int scaledWidth, int scaledHeight, int downscaleLevel)
    {
        // Textures only depend on the size, not on the position of the zone. A restart disposes them, so they're never left with another rotation.
        (int unscaledWidth, int unscaledHeight) = PixelHelper.GetRotatedSize(region.Width, region.Height, display.Rotation);
        (int width, int height) = PixelHelper.GetRotatedSize(scaledWidth, scaledHeight, display.Rotation);

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
            scalingTexture = _device!.CreateTexture2D(scalingTextureDesc);
            scalingTextureView = _device.CreateShaderResourceView(scalingTexture);
        }

        return new ZoneTextures(region.Width, region.Height, downscaleLevel, width, height, stagingTexture, scalingTexture, scalingTextureView);
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
                    if (display.Rotation is ImageRotation.Rotate90 or ImageRotation.Rotate270)
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

        // Unknown until the new duplication delivers the next pointer update.
        _cursorShape = null;
        _cursorVisible = false;
    }

    #endregion

    private sealed class ZoneTextures : IDisposable
    {
        #region Properties & Fields

        // The zone size (display orientation) the textures were created for.
        public int UnscaledWidth { get; }
        public int UnscaledHeight { get; }
        public int DownscaleLevel { get; }

        // The size of the staging texture (native orientation of the output).
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