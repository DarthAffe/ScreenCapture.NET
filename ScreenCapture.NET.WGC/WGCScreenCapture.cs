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
/// Unlike DX11, it can capture a display driven by another graphics card than the one the process renders on (e.g. laptops with two GPUs).
/// Windows 10 marks a captured display with a border; on Windows 11 it's only drawn with <see cref="WGCCaptureOptions.ShowBorder"/>.
/// <para>
/// Since Windows 11 24H2 frames come up to the refresh rate of the display, older versions deliver at most about 60 per second.
/// </para>
/// <para>
/// A display mode change makes the capture restart (see <see cref="IScreenCapture.DisplayChanged"/>).
/// </para>
/// <para>
/// It can capture a window instead (see <see cref="WGCScreenCaptureService.GetWindowCapture(nint)"/>). The <see cref="IScreenCapture.Display"/> then describes the window:
/// the size of its frames (with the window frame, without the shadow), its position on the desktop and its title as <see cref="Display.FriendlyName"/>.
/// <see cref="Display.DeviceName"/> and <see cref="Display.GraphicsCard"/> are the ones of the display it's on.
/// It's read again when the window is resized or moved to another display; a window moved on the same display keeps its old position.
/// Covered windows are captured, minimized ones have no new frames, closed ones make the capture <see cref="CaptureResult.Unavailable"/>.
/// </para>
/// </remarks>
// ReSharper disable once InconsistentNaming
public sealed class WGCScreenCapture : AbstractScreenCapture<ColorBGRA>
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

    // The default (0) caps at about 60 fps, and so do values below 1 ms (https://github.com/robmikh/Win32CaptureSample/issues/82).
    private static readonly TimeSpan MinUpdateInterval = TimeSpan.FromMilliseconds(1);

    #endregion

    #region Properties & Fields

    // Guards the DirectX and capture objects, the zone pipeline and the immediate context. The pipeline's own lock is taken after this one.
    private readonly Lock _captureLock = new();

    /// <summary>
    /// Gets the options this capture was created with.
    /// </summary>
    public WGCCaptureOptions Options { get; }

    /// <summary>
    /// Gets the handle (<c>HWND</c>) of the captured window; 0 if this capture shows a display.
    /// </summary>
    public nint Window { get; }

    // Always 16 bits with HDR: a display can switch to HDR without a mode change.
    private int FramePoolFormat => Options.CaptureHdr ? PIXEL_FORMAT_RGBA16F : PIXEL_FORMAT_BGRA8;

    // For errors.
    private string SourceName => Window != 0 ? $"window 0x{(long)Window:X}" : $"display '{Display.Id}'";

    // In ms, an int so it's atomic.
    private int _timeout;

    /// <summary>
    /// Gets or sets how long <see cref="IScreenCapture.CaptureScreen"/> waits for a new frame before it returns <see cref="CaptureResult.Timeout"/>.
    /// </summary>
    /// <remarks>
    /// Starts with <see cref="WGCCaptureOptions.Timeout"/> and can be changed at any time. Rounded up to whole milliseconds; 0 doesn't wait.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative (including <see cref="System.Threading.Timeout.InfiniteTimeSpan"/>) or greater than <see cref="WGCCaptureOptions.MaxTimeout"/>.</exception>
    public TimeSpan Timeout
    {
        get => TimeSpan.FromMilliseconds(_timeout);
        set => _timeout = CaptureTimeout.ToMilliseconds(CaptureTimeout.Validate(value));
    }

    private readonly IDXGIFactory1 _factory;

    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;

    // IDirect3DDevice, IGraphicsCaptureItem, IDirect3D11CaptureFramePool, IGraphicsCaptureSession.
    private WinRTHandle? _direct3DDevice;
    private WinRTHandle? _item;
    private WinRTHandle? _framePool;
    private WinRTHandle? _session;

    // The fields below are only used under _captureLock.
    private SizeInt32 _frameSize;

    // For the SDR white level of HDR frames.
    private WindowsDisplayDevices.MonitorTarget? _monitorTarget;

    // The HMONITOR a captured window was on when the capture started.
    private nint _monitor;

    private readonly D3D11ZonePipeline _zonePipeline;

    private long? _frameArrivedToken;
    private long? _itemClosedToken;

    // Leaf lock for the fields below, waited on with Monitor.
    private readonly object _frameLock = new();

    // The newest IDirect3D11CaptureFrame not captured yet. Frames are taken out of the pool right away,
    // since Windows drops new frames while the pool is full, and a capture after a pause would show an old screen.
    private nint _newestFrame;

    // Events of a frame pool replaced by a restart are ignored.
    private int _generation;

    // E.g. the display was disconnected.
    private bool _itemClosed;

    #endregion

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="WGCScreenCapture"/> class.
    /// </summary>
    /// <param name="factory">The <see cref="IDXGIFactory1"/> used to find the display.</param>
    /// <param name="display">The <see cref="Display"/> to capture, or one with the id of a window (<see cref="WindowInterop.GetWindowId"/>).</param>
    /// <param name="options">The options of the capture.</param>
    internal WGCScreenCapture(IDXGIFactory1 factory, Display display, WGCCaptureOptions options)
        : base(GetInitialDisplay(factory, display))
    {
        this._factory = factory;
        this.Options = options;
        this.Window = WindowInterop.TryGetWindow(display.Id, out nint window) ? window : 0;
        this._timeout = CaptureTimeout.ToMilliseconds(options.Timeout);
        this._zonePipeline = new D3D11ZonePipeline();

        Restart();
    }

    #endregion

    #region Methods

    // Without a window (or its display) only the id is known, and the first restart fails.
    private static Display GetInitialDisplay(IDXGIFactory1 factory, Display display)
    {
        if (!WindowInterop.TryGetWindow(display.Id, out nint window)) return display;
        if (!WindowInterop.IsTopLevelWindow(window)) return WindowInterop.CreateUnknownDisplay(window);

        if (DXGIDevices.FindDisplay(factory, WindowInterop.GetMonitor(window)) is not { } found)
            return WindowInterop.CreateUnknownDisplay(window);

        found.Adapter.Dispose();
        found.Output.Dispose();
        Display monitorDisplay = found.Display;

        // Usually the size of the frames, the first restart corrects it otherwise.
        (int width, int height) = WindowInterop.GetSize(window);
        return WindowInterop.CreateDisplay(window, monitorDisplay, width, height);
    }

    /// <inheritdoc />
    protected override CaptureResult PerformScreenCapture(out long timestamp)
    {
        timestamp = 0;

        lock (_captureLock)
        {
            if ((_device == null) || (_framePool == null) || !_zonePipeline.IsInitialized)
                return RestartCapture();

            // E.g. the display was disconnected or the window closed.
            bool itemClosed;
            lock (_frameLock)
                itemClosed = _itemClosed;

            if (itemClosed)
            {
                ReportError(new InvalidOperationException($"The capture of the {SourceName} was closed."));
                return RestartCapture();
            }

            // A window moved to another display needs its device and display.
            if ((Window != 0) && (WindowInterop.GetMonitor(Window) != _monitor))
                return RestartCapture();

            // E.g. after a driver update or a GPU reset.
            if (_device.DeviceRemovedReason is { Failure: true } removedReason)
            {
                ReportError(new InvalidOperationException($"The Direct3D device of the capture was removed ({removedReason})."));
                return RestartCapture();
            }

            CaptureResult result = AcquireNextFrame(out timestamp);
            if (result is CaptureResult.NewFrame or CaptureResult.NoChange or CaptureResult.Timeout)
                _zonePipeline.QueueZoneCopies(CaptureZones);

            return result;
        }
    }

    private CaptureResult AcquireNextFrame(out long timestamp)
    {
        timestamp = 0;

        long deadline = Stopwatch.GetTimestamp() + ((_timeout * Stopwatch.Frequency) / 1000);
        nint frame = WaitForFrame(deadline);
        if (frame == 0) return CaptureResult.Timeout;

        SizeInt32 contentSize;
        bool sizeChanged;
        bool outdated = false;
        try
        {
            // A mode change or a resized window.
            contentSize = GetContentSize(frame);
            sizeChanged = contentSize != _frameSize;

            // Only a frame with the current window size is a resize: minimized windows show up as their title bar (e.g. 160x28), also right after restoring.
            if (sizeChanged && (Window != 0) && (WindowInterop.IsMinimized(Window) || (WindowInterop.GetSize(Window) != (contentSize.Width, contentSize.Height))))
            {
                sizeChanged = false;
                outdated = true;
            }
            else if (!sizeChanged)
            {
                // The texture can be larger than its content.
                using (ID3D11Texture2D texture = GetTexture(frame))
                    // A frame from right before the pool got the new size of a resized window.
                    if ((texture.Description.Width < _frameSize.Width) || (texture.Description.Height < _frameSize.Height))
                        outdated = true;
                    else
                        _zonePipeline.SetFrame(texture, _monitorTarget);

                timestamp = ToStopwatchTimestamp(GetSystemRelativeTime(frame));
            }
        }
        finally
        {
            ReleaseFrame(frame);
        }

        // After the frame went back to the pool.
        if (sizeChanged) return Window != 0 ? ResizeWindowCapture(contentSize) : RestartCapture();
        return outdated ? CaptureResult.NoChange : CaptureResult.NewFrame;
    }

    /// <summary>
    /// Restarts the capture of a resized window, keeping device, item and session (a full restart takes about 200 ms).
    /// </summary>
    private CaptureResult ResizeWindowCapture(SizeInt32 size)
    {
        try
        {
            if ((size.Width <= 0) || (size.Height <= 0))
                throw new InvalidOperationException($"The capture of the {SourceName} is {size.Width}x{size.Height}.");

            // Frames that arrived meanwhile have the old size.
            nint newestFrame;
            lock (_frameLock)
            {
                newestFrame = _newestFrame;
                _newestFrame = 0;
            }

            if (newestFrame != 0)
                ReleaseFrame(newestFrame);

            RecreateFramePool(_framePool!, _direct3DDevice!, FramePoolFormat, FRAME_POOL_BUFFERS, size);
            _frameSize = size;

            // Still on the same display.
            Display display = WindowInterop.CreateDisplay(Window, Display, size.Width, size.Height);
            _zonePipeline.Initialize(_device!, _context!, display, ImageRotation.None);
            SetDisplay(display);

            return CaptureResult.Restarting;
        }
        catch (Exception ex)
        {
            ReportError(ex);
            return RestartCapture();
        }
    }

    /// <summary>
    /// Takes the newest frame, waiting until <paramref name="deadline"/> (a <see cref="Stopwatch"/> timestamp) if there's none yet.
    /// </summary>
    /// <returns>The frame (released by the caller), or 0 if none arrived in time or the item closed.</returns>
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

                // Rounded up, waking early would only wait again.
                Monitor.Wait(_frameLock, (int)Math.Min(int.MaxValue, ((remaining * 1000) + Stopwatch.Frequency - 1) / Stopwatch.Frequency));
            }
    }

    // SystemRelativeTime is QueryPerformanceCounter in TimeSpan ticks.
    private static long ToStopwatchTimestamp(long systemRelativeTime)
        => (long)(((Int128)systemRelativeTime * Stopwatch.Frequency) / TimeSpan.TicksPerSecond);

    // On a thread of Windows. The frame pool pointer is only valid during the call.
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

            // Back to the pool, so Windows can write the next frame.
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

        // Guards the immediate context against a concurrent capture or restart.
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
            // Nothing created here may outlive Dispose.
            if (IsDisposed) return false;

            try
            {
                DisposeWGC();

                (Display display, SizeInt32 size) = Window != 0 ? StartWindow() : StartDisplay();

                _zonePipeline.Initialize(_device!, _context!, display, ImageRotation.None);

                int generation;
                lock (_frameLock)
                {
                    generation = ++_generation;
                    _itemClosed = false;
                }

                _itemClosedToken = AddClosed(_item!, new ItemClosedHandler(() => OnItemClosed(generation)));

                _framePool = CreateFreeThreadedFramePool(_direct3DDevice!, FramePoolFormat, FRAME_POOL_BUFFERS, size);
                _frameArrivedToken = AddFrameArrived(_framePool, new FrameArrivedHandler(framePool => OnFrameArrived(framePool, generation)));
                _frameSize = size;

                _session = CreateCaptureSession(_framePool, _item!);

                // Before Windows 10 2004 the pointer is always drawn.
                TrySetCursorCaptureEnabled(_session, Options.DrawCursor);
                TrySetBorderRequired(_session, Options.ShowBorder);
                TrySetMinUpdateInterval(_session, MinUpdateInterval);
                StartCapture(_session);

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

    private (Display Display, SizeInt32 Size) StartDisplay()
    {
        (IDXGIAdapter1 foundAdapter, IDXGIOutput foundOutput, Display display, _monitorTarget) = DXGIDevices.FindDisplay(_factory, Display.Id)
                                                                                              ?? throw new InvalidOperationException($"The display '{Display.Id}' isn't connected.");
        using IDXGIAdapter1 adapter = foundAdapter;
        using IDXGIOutput output = foundOutput;

        CreateDevice(adapter);

        _item = CreateItemForMonitor(output.Description.Monitor);
        SizeInt32 size = GetSize(_item);

        // The frames are already rotated.
        if ((size.Width != display.Width) || (size.Height != display.Height))
            throw new InvalidOperationException($"The capture of the display '{display.Id}' is {size.Width}x{size.Height} instead of {display.Width}x{display.Height}.");

        return (display, size);
    }

    private (Display Display, SizeInt32 Size) StartWindow()
    {
        if (!WindowInterop.IsTopLevelWindow(Window)) throw new InvalidOperationException($"The {SourceName} doesn't exist anymore.");

        _monitor = WindowInterop.GetMonitor(Window);
        (IDXGIAdapter1 foundAdapter, IDXGIOutput foundOutput, Display monitorDisplay, _monitorTarget) = DXGIDevices.FindDisplay(_factory, _monitor)
                                                                                                     ?? throw new InvalidOperationException($"The display the {SourceName} is on wasn't found.");
        using IDXGIAdapter1 adapter = foundAdapter;
        using IDXGIOutput _ = foundOutput;

        CreateDevice(adapter);

        try
        {
            _item = CreateItemForWindow(Window);
        }
        catch (ArgumentException ex)
        {
            // E_INVALIDARG for windows WGC doesn't offer (e.g. tool windows or the taskbar).
            throw new InvalidOperationException($"Windows.Graphics.Capture can't capture the {SourceName} (e.g. a tool window).", ex);
        }

        SizeInt32 size = GetSize(_item);
        if ((size.Width <= 0) || (size.Height <= 0))
            throw new InvalidOperationException($"The capture of the {SourceName} is {size.Width}x{size.Height}.");

        return (WindowInterop.CreateDisplay(Window, monitorDisplay, size.Width, size.Height), size);
    }

    // On the adapter of the display, so the frames aren't copied between adapters.
    private void CreateDevice(IDXGIAdapter1 adapter)
    {
        D3D11.D3D11CreateDevice(adapter, DriverType.Unknown, DeviceCreationFlags.BgraSupport, FEATURE_LEVELS, out _device).CheckError();
        _context = _device!.ImmediateContext;
        _direct3DDevice = CreateDirect3DDevice(_device);
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        lock (_captureLock)
            DisposeWGC();

        base.Dispose(disposing);
    }

    private void DisposeWGC()
    {
        // Handlers first. Events still running are ignored once the generation changed.
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
        _monitor = 0;
    }

    #endregion
}
