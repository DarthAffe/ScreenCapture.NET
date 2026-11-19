using System;
using System.Collections.Generic;
using System.Threading;
using HPPH;
using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
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
public sealed class DX11ScreenCapture : AbstractScreenCapture<ColorBGRA>, ID3D11ZonePipelineHost
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

    // Guards the DirectX objects, the zone pipeline and the immediate context (which isn't thread-safe): held by captures, zone updates and restarts.
    // The zone pipeline guards its zone textures with a lock of its own, acquired after this one; Unregister takes only that one.
    private readonly Lock _captureLock = new();

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

    // Holds the frame and writes the zones from it. Initialized by every restart with the new device, released before the device is disposed.
    private readonly D3D11ZonePipeline _zonePipeline;

    // The monitor of the display, to read its SDR white level for the tone mapping (null if unknown). Only used under _captureLock.
    private WindowsDisplayDevices.MonitorTarget? _monitorTarget;

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
        this._zonePipeline = new D3D11ZonePipeline(this);

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
            if ((_duplicatedOutput == null) || !_zonePipeline.IsInitialized)
                return RestartCapture();

            try
            {
                CaptureResult result = AcquireFrame(_duplicatedOutput, out timestamp);
                if (result is CaptureResult.NewFrame or CaptureResult.NoChange or CaptureResult.Timeout)
                    _zonePipeline.QueueZoneCopies();

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

    private CaptureResult AcquireFrame(IDXGIOutputDuplication duplicatedOutput, out long timestamp)
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
                if (cursorChanged && _zonePipeline.HasFrame)
                {
                    timestamp = frameInfo.LastMouseUpdateTime;
                    return CaptureResult.NewFrame;
                }

                return CaptureResult.NoChange;
            }

            using ID3D11Texture2D screenTexture = screenResource.QueryInterface<ID3D11Texture2D>();
            if (Options.CaptureHdr && (screenTexture.Description.Format == Format.R16G16B16A16_Float))
            {
                // Read on every frame (a few microseconds): the user can change the SDR content brightness at any time, without a mode change.
                float? sdrWhiteLevel = (_monitorTarget is { } monitorTarget) && monitorTarget.TryGetSdrWhiteLevel(out float whiteLevel) ? whiteLevel : null;
                _zonePipeline.ToneMapFrame(screenTexture, sdrWhiteLevel);
            }
            else
                _zonePipeline.CopyFrame(screenTexture);

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
        {
            if (!_zonePipeline.WriteZone(captureZone, buffer)) return false;

            // Into the zone's pixels, not the staging texture: that keeps the frame for writes with the pointer somewhere else.
            if (_cursorVisible && (_cursorShape != null))
                _cursorShape.Draw(buffer, captureZone.Region, captureZone.Width, captureZone.Height, captureZone.DownscaleLevel, _cursorX, _cursorY);
        }

        return true;
    }

    /// <inheritdoc />
    public override bool UnregisterCaptureZone(CaptureZone<ColorBGRA> captureZone)
    {
        if (!base.UnregisterCaptureZone(captureZone)) return false;

        _zonePipeline.RemoveZone(captureZone);

        return true;
    }

    /// <inheritdoc />
    protected override bool PerformRestart()
    {
        lock (_captureLock)
        {
            // Dispose releases everything under this lock after setting the flag: nothing created here may outlive it.
            if (_isDisposed) return false;

            try
            {
                DisposeDX();

                // Found by its id and read again: after a mode change (resolution, rotation) the display has other values,
                // and after displays were added or removed the indices can point to another adapter or output.
                (IDXGIAdapter1 foundAdapter, IDXGIOutput foundOutput, Display display, _monitorTarget) = DXGIDevices.FindDisplay(_factory, Display.Id)
                                                                                                     ?? throw new InvalidOperationException($"The display '{Display.Id}' isn't connected.");
                using IDXGIAdapter1 adapter = foundAdapter;
                using IDXGIOutput dxgiOutput = foundOutput;

                D3D11.D3D11CreateDevice(adapter, DriverType.Unknown, DeviceCreationFlags.None, FEATURE_LEVELS, out _device).CheckError();
                _context = _device!.ImmediateContext;

                using IDXGIOutput5 output = dxgiOutput.QueryInterface<IDXGIOutput5>();

                // The duplication delivers the frames in the native orientation of the output.
                _zonePipeline.Initialize(_device, _context, display, display.Rotation);

                // While the display shows HDR, DuplicateOutput1 delivers 16-bit frames (tone mapped by the capture), otherwise 8-bit ones.
                // DuplicateOutput always delivers 8-bit frames, converted by Windows.
                if (Options.CaptureHdr)
                    _duplicatedOutput = output.DuplicateOutput1(_device, [Format.R16G16B16A16_Float, Format.B8G8R8A8_UNorm]);
                else
                    _duplicatedOutput = output.DuplicateOutput(_device);

                // Zone writes take the display of the capture texture from the pipeline: the zones get this one after the capture.
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
            DisposeDX();
    }

    private void DisposeDX()
    {
        try { _duplicatedOutput?.Dispose(); } catch { /**/ }
        _zonePipeline.Release();
        try { _context?.Dispose(); } catch { /**/ }
        try { _device?.Dispose(); } catch { /**/ }

        _duplicatedOutput = null;
        _context = null;
        _device = null;

        // Unknown until the new duplication delivers the next pointer update.
        _cursorShape = null;
        _cursorVisible = false;
    }

    #endregion

    #region ID3D11ZonePipelineHost

    IReadOnlyList<CaptureZone<ColorBGRA>> ID3D11ZonePipelineHost.CaptureZones => CaptureZones;

    (int width, int height, int downscaleLevel) ID3D11ZonePipelineHost.CalculateScaledSize(int width, int height, int downscaleLevel)
        => CalculateScaledSize(width, height, downscaleLevel);

    CaptureRegion ID3D11ZonePipelineHost.GetSourceRegion(CaptureRegion region, Display display) => GetSourceRegion(region, display);

    void ID3D11ZonePipelineHost.WriteCaptureZoneFromScaled(RefImage<ColorBGRA> scaled, CaptureZone<ColorBGRA> captureZone, Span<ColorBGRA> buffer, ImageRotation rotation)
        => WriteCaptureZoneFromScaled(scaled, captureZone, buffer, rotation);

    void ID3D11ZonePipelineHost.ReportError(Exception exception) => ReportError(exception);

    #endregion
}
