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
/// <para>
/// It can capture a window instead of a display (see <see cref="WGCScreenCaptureService.GetWindowCapture(nint)"/> and <see cref="Window"/>). Its <see cref="IScreenCapture.Display"/> then describes the window:
/// its size is the one of the frames (the window as it's shown, with its frame but without the shadow), the position is the one on the desktop,
/// <see cref="Display.FriendlyName"/> is its title, and <see cref="Display.DeviceName"/> and <see cref="Display.GraphicsCard"/> are the ones of the display it's on.
/// These are read when the capture starts or restarts: when the window is resized or moved to another display, so the zones are relative to the window as it's now.
/// A window that's only moved keeps its old <see cref="Display.X"/> and <see cref="Display.Y"/>.
/// The window is captured also while it's covered by others; a minimized window has no new frames (<see cref="CaptureResult.Timeout"/>).
/// After the window was closed the capture is <see cref="CaptureResult.Unavailable"/>.
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

    #endregion

    #region Properties & Fields

    // Guards the DirectX and capture objects, the zone pipeline and the immediate context (which isn't thread-safe): held by captures, zone updates and restarts.
    // The zone pipeline guards its zone textures with a lock of its own, acquired after this one; Unregister takes only that one.
    private readonly Lock _captureLock = new();

    /// <summary>
    /// Gets the options this capture was created with.
    /// </summary>
    public WGCCaptureOptions Options { get; }

    /// <summary>
    /// Gets the handle (<c>HWND</c>) of the captured window; 0 if this capture shows a display.
    /// </summary>
    /// <remarks>
    /// See <see cref="WGCScreenCaptureService.GetWindowCapture(nint)"/>.
    /// </remarks>
    public nint Window { get; }

    // In HDR every display is captured in 16 bits: a display can switch to HDR without a mode change (and a window move to another display).
    private int FramePoolFormat => Options.CaptureHdr ? PIXEL_FORMAT_RGBA16F : PIXEL_FORMAT_BGRA8;

    // How the source is named in errors.
    private string SourceName => Window != 0 ? $"window 0x{(long)Window:X}" : $"display '{Display.Id}'";

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

    // The monitor (HMONITOR) a captured window was on when the capture started. Only used under _captureLock.
    private nint _monitor;

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
    /// <param name="display">
    /// The <see cref="Display"/> to capture, or the one of a window (its id from <see cref="WindowInterop.GetWindowId"/>; the other values are read from the window).
    /// </param>
    /// <param name="options">The options of the capture.</param>
    internal WGCScreenCapture(IDXGIFactory1 factory, Display display, WGCCaptureOptions options)
        : base(GetInitialDisplay(factory, display))
    {
        this._factory = factory;
        this.Options = options;
        this.Window = WindowInterop.TryGetWindow(display.Id, out nint window) ? window : 0;
        this._timeout = WGCCaptureOptions.ToMilliseconds(options.Timeout);
        this._zonePipeline = new D3D11ZonePipeline();

        Restart();
    }

    #endregion

    #region Methods

    // A window is described as it is now; without a window (or its display) only the id is known, and the first restart fails.
    private static Display GetInitialDisplay(IDXGIFactory1 factory, Display display)
    {
        if (!WindowInterop.TryGetWindow(display.Id, out nint window)) return display;
        if (!WindowInterop.IsTopLevelWindow(window)) return WindowInterop.CreateUnknownDisplay(window);

        if (DXGIDevices.FindDisplay(factory, WindowInterop.GetMonitor(window)) is not { } found)
            return WindowInterop.CreateUnknownDisplay(window);

        found.Adapter.Dispose();
        found.Output.Dispose();
        Display monitorDisplay = found.Display;

        // The size of the frames is only known once the capture runs; usually it's the one of the window (the first restart corrects it otherwise).
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

            // E.g. the display was disconnected or the window closed: there won't be any frames anymore.
            bool itemClosed;
            lock (_frameLock)
                itemClosed = _itemClosed;

            if (itemClosed)
            {
                ReportError(new InvalidOperationException($"The capture of the {SourceName} was closed."));
                return RestartCapture();
            }

            // A window moved to another display: read again, so the display (with its graphics card and SDR white level) and the device are the ones of the new one.
            if ((Window != 0) && (WindowInterop.GetMonitor(Window) != _monitor))
                return RestartCapture();

            // E.g. after a driver update or a GPU reset.
            if (_device.DeviceRemovedReason is { Failure: true } removedReason)
            {
                ReportError(new InvalidOperationException($"The Direct3D device of the capture was removed ({removedReason})."));
                return RestartCapture();
            }

            CaptureResult result = AcquireFrame(out timestamp);
            if (result is CaptureResult.NewFrame or CaptureResult.NoChange or CaptureResult.Timeout)
                _zonePipeline.QueueZoneCopies(CaptureZones);

            return result;
        }
    }

    private CaptureResult AcquireFrame(out long timestamp)
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
            // A mode change (resolution, rotation) or a resized window: the source has to be read again, with a frame pool of the new size.
            contentSize = GetContentSize(frame);
            sizeChanged = contentSize != _frameSize;

            // Only a frame with the size the window has now is a resize: a minimized window can show up as its minimized title bar (e.g. 160x28), also in a frame after it was restored.
            if (sizeChanged && (Window != 0) && (WindowInterop.IsMinimized(Window) || (WindowInterop.GetSize(Window) != (contentSize.Width, contentSize.Height))))
            {
                sizeChanged = false;
                outdated = true;
            }
            else if (!sizeChanged)
            {
                // Frames are in the orientation of the display. The texture of the frame pool can be larger than its content (it's copied from the top left).
                using (ID3D11Texture2D texture = GetTexture(frame))
                    // A frame the event took out of the frame pool right before its buffers got the new size of a resized window: it doesn't fit.
                    if ((texture.Description.Width < _frameSize.Width) || (texture.Description.Height < _frameSize.Height))
                        outdated = true;
                    else if (texture.Description.Format == Format.R16G16B16A16_Float)
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

        // After the frame went back to the frame pool the restart disposes (or recreates).
        if (sizeChanged) return Window != 0 ? ResizeWindowCapture(contentSize) : RestartCapture();
        return outdated ? CaptureResult.NoChange : CaptureResult.NewFrame;
    }

    /// <summary>
    /// Restarts the capture of a resized window: the device, the capture item and the session stay (a full restart takes about 200 ms, most of it creating the device),
    /// only the buffers of the frame pool and the zone textures get the new size, and the window is read again.
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

            // Still on the same display (a move to another one restarts): its device name and graphics card stay.
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

    // Creates the device and the capture item of the display. Returns the display as it is now and the size of its frames.
    private (Display Display, SizeInt32 Size) StartDisplay()
    {
        // Found by its id and read again: after a mode change (resolution, rotation) the display has other values,
        // and after displays were added or removed the indices can point to another adapter or output.
        (IDXGIAdapter1 foundAdapter, IDXGIOutput foundOutput, Display display, _monitorTarget) = DXGIDevices.FindDisplay(_factory, Display.Id)
                                                                                              ?? throw new InvalidOperationException($"The display '{Display.Id}' isn't connected.");
        using IDXGIAdapter1 adapter = foundAdapter;
        using IDXGIOutput output = foundOutput;

        CreateDevice(adapter);

        _item = CreateItemForMonitor(output.Description.Monitor);
        SizeInt32 size = GetSize(_item);

        // The frames show the display as it's shown (in its orientation), so the zones need no rotation.
        if ((size.Width != display.Width) || (size.Height != display.Height))
            throw new InvalidOperationException($"The capture of the display '{display.Id}' is {size.Width}x{size.Height} instead of {display.Width}x{display.Height}.");

        return (display, size);
    }

    // Creates the device and the capture item of the window. Returns the display describing the window and the size of its frames.
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
            // E_INVALIDARG for windows it doesn't offer for capturing (e.g. tool windows and ones that can't be activated, like the taskbar).
            throw new InvalidOperationException($"Windows.Graphics.Capture can't capture the {SourceName} (e.g. a tool window).", ex);
        }

        SizeInt32 size = GetSize(_item);
        if ((size.Width <= 0) || (size.Height <= 0))
            throw new InvalidOperationException($"The capture of the {SourceName} is {size.Width}x{size.Height}.");

        // The frames show the window as it's shown: the size is the one of the frames.
        return (WindowInterop.CreateDisplay(Window, monitorDisplay, size.Width, size.Height), size);
    }

    // On the adapter of the display, so its frames don't have to be copied between adapters (Windows would do that for any other one).
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
        _monitor = 0;
    }

    #endregion
}
