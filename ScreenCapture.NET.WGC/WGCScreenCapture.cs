using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using HPPH;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using static ScreenCapture.NET.WGCInterop;

namespace ScreenCapture.NET;

/// <summary>
/// Represents a ScreenCapture using Windows.Graphics.Capture.
/// https://learn.microsoft.com/en-us/windows/uwp/audio-video-camera/screen-capture
/// </summary>
/// <remarks>
/// Unlike Desktop Duplication (DX11), it can capture a display driven by another graphics card than the one the process renders on (e.g. on laptops with two GPUs).
/// Windows 10 marks a display that's captured with a border (not part of the frames, but of what other captures like DX11 see); on Windows 11 it's only drawn with <see cref="WGCCaptureOptions.ShowBorder"/>.
/// <para>
/// The timestamp of a frame (<see cref="ScreenCaptureUpdatedEventArgs.Timestamp"/>) is when it's shown on the display, which can be up to a refresh after the capture got it.
/// </para>
/// <para>
/// A display mode change (resolution, rotation) changes the size of the frames: the next capture restarts and reads the display again (see <see cref="IScreenCapture.DisplayChanged"/>).
/// </para>
/// </remarks>
// ReSharper disable once InconsistentNaming
public sealed class WGCScreenCapture : AbstractScreenCapture<ColorBGRA>, ID3D11ZonePipelineHost
{
    #region Constants

    private static readonly FeatureLevel[] FEATURE_LEVELS =
    [
        FeatureLevel.Level_11_1,
        FeatureLevel.Level_11_0,
        FeatureLevel.Level_10_1,
        FeatureLevel.Level_10_0
    ];

    // The frame Windows writes while the capture reads the last one.
    private const int FRAME_POOL_BUFFERS = 2;

    #endregion

    #region Properties & Fields

    // Guards the DirectX and capture objects, the zone pipeline and the immediate context (which isn't thread-safe): held by captures, zone updates and restarts.
    // The zone pipeline guards its zone textures with a lock of its own, acquired after this one; Unregister takes only that one.
    private readonly Lock _captureLock = new();

    private bool _isDisposed;

    /// <summary>
    /// Gets the options this capture was created with.
    /// </summary>
    public WGCCaptureOptions Options { get; }

    // In whole milliseconds; an int, so it's read and written atomically.
    private int _timeout;

    /// <summary>
    /// Gets or sets how long <see cref="IScreenCapture.CaptureScreen"/> waits for a new frame before it returns <see cref="CaptureResult.Timeout"/>.
    /// </summary>
    /// <remarks>
    /// It starts with <see cref="WGCCaptureOptions.Timeout"/> and can be changed at any time; a running capture keeps the value it started with.
    /// It's rounded up to whole milliseconds; 0 doesn't wait.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative (including <see cref="System.Threading.Timeout.InfiniteTimeSpan"/>) or greater than <see cref="WGCCaptureOptions.MaxTimeout"/>.</exception>
    public TimeSpan Timeout
    {
        get => TimeSpan.FromMilliseconds(_timeout);
        set => _timeout = WGCCaptureOptions.ToMilliseconds(WGCCaptureOptions.ValidateTimeout(value));
    }

    private readonly IDXGIFactory1 _factory;

    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;

    // The WinRT objects: IDirect3DDevice, IGraphicsCaptureItem, IDirect3D11CaptureFramePool, IGraphicsCaptureSession.
    private WinRTHandle? _direct3DDevice;
    private WinRTHandle? _item;
    private WinRTHandle? _framePool;
    private WinRTHandle? _session;

    // The size of the frames when the capture started (the one of the display). Only used under _captureLock.
    private SizeInt32 _frameSize;

    // Reads the SDR white level of the display for HDR frames; null if Windows has no target for it. Only used under _captureLock.
    private WindowsDisplayDevices.MonitorTarget? _monitorTarget;

    // Holds the frame and writes the zones from it. Initialized by every restart with the new device, released before the device is disposed.
    private readonly D3D11ZonePipeline _zonePipeline;

    // The registrations of the handlers (raised on threads of Windows), to remove them again. Only used under _captureLock.
    private long? _frameArrivedToken;
    private long? _itemClosedToken;

    // Guards the fields below; waited on with Monitor (System.Threading.Lock can't be waited on). A leaf lock: nothing else is acquired while holding it.
    private readonly object _frameLock = new();

    // The newest frame (IDirect3D11CaptureFrame, 0 if none) that arrived and wasn't captured yet. The handler takes every frame out of the frame pool right away
    // and drops the older one: Windows drops new frames while the frame pool has no free buffer, so frames left in it would make a capture after a pause show the screen from before it.
    private nint _newestFrame;

    // Counts the frame pools (and items) of the restarts: events of an older one that are still running when a restart replaced it are ignored.
    private int _generation;

    // Set when the capture item closes (e.g. the display was disconnected): the next capture restarts.
    private bool _itemClosed;

    #endregion

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="WGCScreenCapture"/> class.
    /// </summary>
    /// <param name="factory">The <see cref="IDXGIFactory1"/> used to find the display.</param>
    /// <param name="display">The <see cref="Display"/> to capture.</param>
    /// <param name="options">The options of the capture.</param>
    internal WGCScreenCapture(IDXGIFactory1 factory, Display display, WGCCaptureOptions options)
        : base(display)
    {
        this._factory = factory;
        this.Options = options;
        this._timeout = WGCCaptureOptions.ToMilliseconds(options.Timeout);
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
            if ((_device == null) || (_framePool == null) || !_zonePipeline.IsInitialized)
                return RestartCapture();

            // E.g. the display was disconnected: there won't be any frames anymore.
            bool itemClosed;
            lock (_frameLock)
                itemClosed = _itemClosed;

            if (itemClosed)
            {
                ReportError(new InvalidOperationException($"The capture of the display '{Display.Id}' was closed."));
                return RestartCapture();
            }

            // E.g. after a driver update or a GPU reset.
            if (_device.DeviceRemovedReason is { Failure: true } removedReason)
            {
                ReportError(new InvalidOperationException($"The Direct3D device of the capture was removed ({removedReason})."));
                return RestartCapture();
            }

            CaptureResult result = AcquireFrame(out timestamp);
            if (result is CaptureResult.NewFrame or CaptureResult.NoChange or CaptureResult.Timeout)
                _zonePipeline.QueueZoneCopies();

            return result;
        }
    }

    private CaptureResult AcquireFrame(out long timestamp)
    {
        timestamp = 0;

        long deadline = Stopwatch.GetTimestamp() + ((_timeout * Stopwatch.Frequency) / 1000);
        nint frame = WaitForFrame(deadline);
        if (frame == 0) return CaptureResult.Timeout;

        bool sizeChanged;
        try
        {
            // A mode change (resolution, rotation): the display has to be read again, with a frame pool of the new size.
            sizeChanged = GetContentSize(frame) != _frameSize;
            if (!sizeChanged)
            {
                // Frames are in the orientation of the display. The texture of the frame pool can be larger than its content (it's copied from the top left).
                using (ID3D11Texture2D texture = GetTexture(frame))
                    if (texture.Description.Format == Format.R16G16B16A16_Float)
                    {
                        // Read on every frame (a few microseconds): the user can change the SDR content brightness at any time, without a mode change.
                        float? sdrWhiteLevel = (_monitorTarget is { } monitorTarget) && monitorTarget.TryGetSdrWhiteLevel(out float whiteLevel) ? whiteLevel : null;
                        _zonePipeline.ToneMapFrame(texture, sdrWhiteLevel);
                    }
                    else
                        _zonePipeline.CopyFrame(texture);

                timestamp = ToStopwatchTimestamp(GetSystemRelativeTime(frame));
            }
        }
        finally
        {
            ReleaseFrame(frame);
        }

        // After the frame went back to the frame pool the restart disposes.
        return sizeChanged ? RestartCapture() : CaptureResult.NewFrame;
    }

    /// <summary>
    /// Takes the newest frame that arrived, waiting until <paramref name="deadline"/> (a <see cref="Stopwatch"/> timestamp) if there's none yet.
    /// </summary>
    /// <returns>The frame (the caller releases it with <see cref="ReleaseFrame"/>), or 0 if none arrived in time or the capture item closed.</returns>
    private nint WaitForFrame(long deadline)
    {
        lock (_frameLock)
            while (true)
            {
                if (_newestFrame != 0)
                {
                    nint frame = _newestFrame;
                    _newestFrame = 0;
                    return frame;
                }

                if (_itemClosed) return 0;

                long remaining = deadline - Stopwatch.GetTimestamp();
                if (remaining <= 0) return 0;

                // Rounded up: waking before the deadline would only wait again.
                Monitor.Wait(_frameLock, (int)Math.Min(int.MaxValue, ((remaining * 1000) + Stopwatch.Frequency - 1) / Stopwatch.Frequency));
            }
    }

    // SystemRelativeTime is the QueryPerformanceCounter time (in TimeSpan ticks), the clock of the Stopwatch.
    private static long ToStopwatchTimestamp(long systemRelativeTime)
        => (long)(((Int128)systemRelativeTime * Stopwatch.Frequency) / TimeSpan.TicksPerSecond);

    // On a thread of Windows; the frame pool pointer is only valid during the call. Exceptions (e.g. the frame pool was closed by a restart meanwhile) end it.
    private void OnFrameArrived(nint framePool, int generation)
    {
        nint frame;
        while ((frame = TryGetNextFrame(framePool)) != 0)
        {
            nint dropped;
            lock (_frameLock)
            {
                if (generation != _generation)
                    dropped = frame;
                else
                {
                    dropped = _newestFrame;
                    _newestFrame = frame;
                    Monitor.PulseAll(_frameLock);
                }
            }

            // Back to the frame pool, so Windows can write the next frame into it.
            if (dropped != 0)
                ReleaseFrame(dropped);
        }
    }

    private void OnItemClosed(int generation)
    {
        lock (_frameLock)
        {
            if (generation != _generation) return;

            _itemClosed = true;
            Monitor.PulseAll(_frameLock);
        }
    }

    /// <inheritdoc />
    protected override bool PerformCaptureZoneUpdate(CaptureZone<ColorBGRA> captureZone, Span<ColorBGRA> buffer)
    {
        ArgumentNullException.ThrowIfNull(captureZone);

        // _captureLock: another thread can capture (or restart) meanwhile, and both use the immediate context.
        lock (_captureLock)
            return _zonePipeline.WriteZone(captureZone, buffer);
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
                DisposeWGC();

                // Found by its id and read again: after a mode change (resolution, rotation) the display has other values,
                // and after displays were added or removed the indices can point to another adapter or output.
                (IDXGIAdapter1 foundAdapter, IDXGIOutput foundOutput, Display display, _monitorTarget) = DXGIDevices.FindDisplay(_factory, Display.Id)
                                                                                                      ?? throw new InvalidOperationException($"The display '{Display.Id}' isn't connected.");
                using IDXGIAdapter1 adapter = foundAdapter;
                using IDXGIOutput output = foundOutput;

                // On the adapter of the display, so its frames don't have to be copied between adapters (Windows would do that for any other one).
                D3D11.D3D11CreateDevice(adapter, DriverType.Unknown, DeviceCreationFlags.BgraSupport, FEATURE_LEVELS, out _device).CheckError();
                _context = _device!.ImmediateContext;
                _direct3DDevice = CreateDirect3DDevice(_device);

                _item = CreateItemForMonitor(output.Description.Monitor);
                SizeInt32 size = GetSize(_item);

                // The frames show the display as it's shown (in its orientation), so the zones need no rotation.
                if ((size.Width != display.Width) || (size.Height != display.Height))
                    throw new InvalidOperationException($"The capture of the display '{display.Id}' is {size.Width}x{size.Height} instead of {display.Width}x{display.Height}.");

                _zonePipeline.Initialize(_device, _context, display, ImageRotation.None);

                int generation;
                lock (_frameLock)
                {
                    generation = ++_generation;
                    _itemClosed = false;
                }

                _itemClosedToken = AddClosed(_item, new ItemClosedHandler(() => OnItemClosed(generation)));

                // In HDR every display is captured in 16 bits: a display can switch to HDR without a mode change.
                _framePool = CreateFreeThreadedFramePool(_direct3DDevice, Options.CaptureHdr ? PIXEL_FORMAT_RGBA16F : PIXEL_FORMAT_BGRA8, FRAME_POOL_BUFFERS, size);
                _frameArrivedToken = AddFrameArrived(_framePool, new FrameArrivedHandler(framePool => OnFrameArrived(framePool, generation)));
                _frameSize = size;

                _session = CreateCaptureSession(_framePool, _item);

                // Without the option Windows would draw the mouse pointer into the frames anyway (before Windows 10 2004 it always does).
                TrySetCursorCaptureEnabled(_session, Options.DrawCursor);
                TrySetBorderRequired(_session, Options.ShowBorder);
                StartCapture(_session);

                // Zone writes take the display of the capture texture from the pipeline: the zones get this one after the capture.
                SetDisplay(display);

                return true;
            }
            catch (Exception ex)
            {
                DisposeWGC();
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
            DisposeWGC();
    }

    private void DisposeWGC()
    {
        // The handlers first. Events already running are ignored after the generation changed, and the frame they took goes back.
        if ((_framePool != null) && (_frameArrivedToken is { } frameArrivedToken))
            try { RemoveFrameArrived(_framePool, frameArrivedToken); } catch { /**/ }
        if ((_item != null) && (_itemClosedToken is { } itemClosedToken))
            try { RemoveClosed(_item, itemClosedToken); } catch { /**/ }

        nint newestFrame;
        lock (_frameLock)
        {
            _generation++;
            newestFrame = _newestFrame;
            _newestFrame = 0;
        }

        if (newestFrame != 0)
            try { ReleaseFrame(newestFrame); } catch { /**/ }
        try { _session?.Dispose(); } catch { /**/ }
        try { _framePool?.Dispose(); } catch { /**/ }
        try { _item?.Dispose(); } catch { /**/ }
        _zonePipeline.Release();
        try { _direct3DDevice?.Dispose(); } catch { /**/ }
        try { _context?.Dispose(); } catch { /**/ }
        try { _device?.Dispose(); } catch { /**/ }

        _session = null;
        _framePool = null;
        _frameArrivedToken = null;
        _item = null;
        _itemClosedToken = null;
        _direct3DDevice = null;
        _context = null;
        _device = null;
        _monitorTarget = null;
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
