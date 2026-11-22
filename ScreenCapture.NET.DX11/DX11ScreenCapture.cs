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
/// A display mode change ends the duplication; the next capture restarts it (see <see cref="IScreenCapture.DisplayChanged"/>).
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

    // Guards the DirectX objects, the zone pipeline and the immediate context. The pipeline's own lock is taken after this one.
    private readonly Lock _captureLock = new();

    /// <summary>
    /// Gets the options this capture was created with.
    /// </summary>
    public DX11CaptureOptions Options { get; }

    // In ms, an int so it's atomic.
    private int _timeout;

    /// <summary>
    /// Gets or sets how long <see cref="IScreenCapture.CaptureScreen"/> waits for a new frame before it returns <see cref="CaptureResult.Timeout"/>.
    /// </summary>
    /// <remarks>
    /// Starts with <see cref="DX11CaptureOptions.Timeout"/> and can be changed at any time. Rounded up to whole milliseconds; 0 doesn't wait.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative (including <see cref="System.Threading.Timeout.InfiniteTimeSpan"/>) or greater than <see cref="DX11CaptureOptions.MaxTimeout"/>.</exception>
    public TimeSpan Timeout
    {
        get => TimeSpan.FromMilliseconds(_timeout);
        set => _timeout = CaptureTimeout.ToMilliseconds(CaptureTimeout.Validate(value));
    }

    private readonly IDXGIFactory1 _factory;

    private IDXGIOutputDuplication? _duplicatedOutput;
    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;

    private readonly D3D11ZonePipeline _zonePipeline;

    // For the SDR white level of HDR frames.
    private WindowsDisplayDevices.MonitorTarget? _monitorTarget;

    // Only with DrawCursor. _cursorX/_cursorY are the top left of the shape on the display.
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
        this._timeout = CaptureTimeout.ToMilliseconds(options.Timeout);
        this._zonePipeline = new D3D11ZonePipeline();

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
            if ((_device == null) || (_duplicatedOutput == null) || !_zonePipeline.IsInitialized)
                return RestartCapture();

            // E.g. after a driver update or a GPU reset (TDR).
            if (_device.DeviceRemovedReason is { Failure: true } removedReason)
            {
                ReportError(new InvalidOperationException($"The Direct3D device of the capture was removed ({removedReason})."));
                return RestartCapture();
            }

            try
            {
                CaptureResult result = AcquireNextFrame(_duplicatedOutput, out timestamp);
                if (result is CaptureResult.NewFrame or CaptureResult.NoChange or CaptureResult.Timeout)
                    _zonePipeline.QueueZoneCopies(CaptureZones);

                return result;
            }
            catch (SharpGenException dxException) when ((dxException.ResultCode == ResultCode.AccessLost)
                                                      || (dxException.ResultCode == ResultCode.AccessDenied)
                                                      || (dxException.ResultCode == ResultCode.InvalidCall)
                                                      || (dxException.ResultCode == ResultCode.DeviceRemoved))
            {
                ReportError(dxException);
                return RestartCapture();
            }
        }
    }

    private CaptureResult AcquireNextFrame(IDXGIOutputDuplication duplicatedOutput, out long timestamp)
    {
        timestamp = 0;

        IDXGIResource? screenResource = null;
        bool acquired = false;
        try
        {
            Result acquireResult = duplicatedOutput.AcquireNextFrame((uint)_timeout, out OutduplFrameInfo frameInfo, out screenResource);
            if (acquireResult == ResultCode.WaitTimeout) 
                return CaptureResult.Timeout;

            acquireResult.CheckError();
            acquired = true;

            // The shape is only available until the frame is released.
            bool cursorChanged = Options.DrawCursor && UpdateCursor(duplicatedOutput, frameInfo);

            // No present since the last frame (e.g. only the mouse cursor moved).
            if ((screenResource == null) || (frameInfo.LastPresentTime == 0))
            {
                // The drawn pointer changed: write the zones again from the last frame.
                if (cursorChanged && _zonePipeline.HasFrame)
                {
                    timestamp = frameInfo.LastMouseUpdateTime;
                    return CaptureResult.NewFrame;
                }

                return CaptureResult.NoChange;
            }

            using ID3D11Texture2D screenTexture = screenResource.QueryInterface<ID3D11Texture2D>();
            _zonePipeline.SetFrame(screenTexture, _monitorTarget);

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
    /// Takes the mouse pointer from an acquired frame. Called under <see cref="_captureLock"/>.
    /// </summary>
    /// <returns><c>true</c> if the pointer drawn into the zones changed.</returns>
    private bool UpdateCursor(IDXGIOutputDuplication duplicatedOutput, in OutduplFrameInfo frameInfo)
    {
        // No pointer update since the last frame.
        if (frameInfo.LastMouseUpdateTime == 0) return false;

        bool wasDrawn = _cursorVisible && (_cursorShape != null);
        bool changed = false;

        if (frameInfo.PointerShapeBufferSize > 0)
        {
            _cursorShape = ReadCursorShape(duplicatedOutput, (int)frameInfo.PointerShapeBufferSize);
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

        uint requiredSize;
        OutduplPointerShapeInfo shapeInfo;
        fixed (byte* buffer = _cursorShapeBuffer)
            duplicatedOutput.GetFramePointerShape((uint)_cursorShapeBuffer.Length, (IntPtr)buffer, out requiredSize, out shapeInfo).CheckError();

        try
        {
            return CursorShape.Create((PointerShapeType)shapeInfo.Type, (int)shapeInfo.Width, (int)shapeInfo.Height, (int)shapeInfo.Pitch, _cursorShapeBuffer.AsSpan(0, Math.Min((int)requiredSize, _cursorShapeBuffer.Length)));
        }
        catch (ArgumentException ex)
        {
            // Not drawn until the next shape.
            ReportError(ex);
            return null;
        }
    }

    /// <inheritdoc />
    protected override bool PerformCaptureZoneUpdate(CaptureZone<ColorBGRA> captureZone, Span<ColorBGRA> buffer)
    {
        ArgumentNullException.ThrowIfNull(captureZone);

        // Guards the immediate context against a concurrent capture or restart.
        lock (_captureLock)
        {
            if (!_zonePipeline.WriteZone(captureZone, buffer)) return false;

            // Into the zone, so the staging texture keeps the clean frame.
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
            // Nothing created here may outlive Dispose.
            if (IsDisposed) return false;

            try
            {
                DisposeDX();

                (IDXGIAdapter1 foundAdapter, IDXGIOutput foundOutput, Display display, _monitorTarget) = DXGIDevices.FindDisplay(_factory, Display.Id)
                                                                                                     ?? throw new InvalidOperationException($"The display '{Display.Id}' isn't connected.");
                using IDXGIAdapter1 adapter = foundAdapter;
                using IDXGIOutput dxgiOutput = foundOutput;

                D3D11.D3D11CreateDevice(adapter, DriverType.Unknown, DeviceCreationFlags.None, FEATURE_LEVELS, out _device).CheckError();
                _context = _device!.ImmediateContext;

                using IDXGIOutput5 output = dxgiOutput.QueryInterface<IDXGIOutput5>();

                // The duplication delivers the native orientation of the output.
                _zonePipeline.Initialize(_device, _context, display, display.Rotation);

                // DuplicateOutput1 delivers 16-bit frames while the display shows HDR. DuplicateOutput always delivers 8-bit frames converted by Windows.
                if (Options.CaptureHdr)
                    _duplicatedOutput = output.DuplicateOutput1(_device, [Format.R16G16B16A16_Float, Format.B8G8R8A8_UNorm]);
                else
                    _duplicatedOutput = output.DuplicateOutput(_device);

                SetDisplay(display);

                return true;
            }
            catch (Exception ex)
            {
                // E.g. while a UAC prompt or the lock screen is shown.
                DisposeDX();
                ReportError(ex);

                return false;
            }
        }
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        lock (_captureLock)
            DisposeDX();

        base.Dispose(disposing);
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

        // Unknown until the next pointer update.
        _cursorShape = null;
        _cursorVisible = false;
    }

    #endregion
}
