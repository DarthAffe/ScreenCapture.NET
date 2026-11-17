using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using HPPH;
using SharpGen.Runtime;
using Vortice.Direct3D9;

namespace ScreenCapture.NET;

/// <summary>
/// Represents a ScreenCapture using DirectX 9.
/// https://learn.microsoft.com/en-us/windows/win32/api/d3d9/nf-d3d9-idirect3ddevice9-getfrontbufferdata
/// </summary>
/// <remarks>
/// Every capture checks the display mode: after a change of the resolution it restarts and reads the display again (see <see cref="IScreenCapture.DisplayChanged"/>).
/// </remarks>
// ReSharper disable once InconsistentNaming
public sealed class DX9ScreenCapture : AbstractScreenCapture<ColorBGRA>
{
    #region Properties & Fields

    private readonly Lock _captureLock = new();

    private readonly IDirect3D9Ex _direct3D9;
    private IDirect3DDevice9? _device;
    private IDirect3DSurface9? _surface;

    // Replaced under _captureLock, but read by zone updates without it: the pixels and their size always belong together.
    // The frame read from the surface, replaced by restarts. Only used under _captureLock.
    private FrameBuffer? _frame;

    private bool _isDisposed;

    /// <summary>
    /// Gets the options this capture was created with.
    /// </summary>
    public DX9CaptureOptions Options { get; }

    #endregion

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="DX9ScreenCapture"/> class.
    /// </summary>
    /// <param name="direct3D9">The D3D9 instance used.</param>
    /// <param name="display">The <see cref="Display"/> to duplicate.</param>
    /// <param name="options">The options of the capture.</param>
    internal DX9ScreenCapture(IDirect3D9Ex direct3D9, Display display, DX9CaptureOptions options)
        : base(display)
    {
        this._direct3D9 = direct3D9;
        this.Options = options;

        Restart();
    }

    #endregion

    #region Methods

    /// <inheritdoc />
    protected override CaptureResult PerformScreenCapture(out long timestamp)
    {
        // DX9 can't tell when the frame was presented: the base uses the time the capture returned.
        timestamp = 0;

        lock (_captureLock)
        {
            FrameBuffer? frame = _frame;
            if ((_device == null) || (_surface == null) || (frame == null))
                return RestartCapture();

            // The surface has the size of the display mode the capture started with; GetFrontBufferData needs the current one.
            if (!IsDisplayModeOf(frame))
                return RestartCapture();

            try
            {
                _device.GetFrontBufferData(0, _surface);

                LockedRectangle dr = _surface.LockRect(LockFlags.NoSystemLock | LockFlags.ReadOnly);

                nint ptr = dr.DataPointer;
                for (int y = 0; y < frame.Height; y++)
                {
                    Marshal.Copy(ptr, frame.Data, y * frame.Stride, frame.Stride);
                    ptr += dr.Pitch;
                }

                _surface.UnlockRect();
                frame.HasData = true;

                // GetFrontBufferData has no change detection: every successful read counts as a new frame.
                return CaptureResult.NewFrame;
            }
            catch (SharpGenException dxException) when (dxException.ResultCode == Result.AccessDenied)
            {
                ReportError(dxException);
                return RestartCapture();
            }
        }
    }

    private bool IsDisplayModeOf(FrameBuffer frame)
    {
        try
        {
            DisplayMode displayMode = _direct3D9.GetAdapterDisplayMode(Display.Index);
            return (displayMode.Width == frame.Width) && (displayMode.Height == frame.Height);
        }
        catch (SharpGenException)
        {
            // E.g. the adapter is gone: the restart finds out what happened to the display.
            return false;
        }
    }

    /// <inheritdoc />
    protected override bool PerformCaptureZoneUpdate(CaptureZone<ColorBGRA> captureZone, Span<byte> buffer)
    {
        ArgumentNullException.ThrowIfNull(captureZone);

        // Another thread can capture (or restart) meanwhile: without the lock it could overwrite the frame while it's copied.
        lock (_captureLock)
        {
            // HasData: e.g. a Restart on another thread replaced the frame after this capture read it.
            FrameBuffer? frame = _frame;
            if ((frame == null) || !frame.HasData || !captureZone.Region.IsWithin(frame.Width, frame.Height)) return false;

            if (captureZone.DownscaleLevel == 0)
                CopyZone(frame.Data, frame.Width, frame.Height, frame.Stride, captureZone, buffer);
            else
                DownscaleZone(frame.Data, frame.Width, frame.Height, frame.Stride, captureZone, buffer);

            return true;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void CopyZone(ReadOnlySpan<byte> frame, int frameWidth, int frameHeight, int frameStride, CaptureZone<ColorBGRA> captureZone, Span<byte> buffer)
    {
        RefImage<ColorBGRA>.Wrap(frame, frameWidth, frameHeight, frameStride)[captureZone.Region.X, captureZone.Region.Y, captureZone.Width, captureZone.Height]
                           .CopyTo(MemoryMarshal.Cast<byte, ColorBGRA>(buffer));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void DownscaleZone(ReadOnlySpan<byte> frame, int frameWidth, int frameHeight, int frameStride, CaptureZone<ColorBGRA> captureZone, Span<byte> buffer)
    {
        RefImage<ColorBGRA> source = RefImage<ColorBGRA>.Wrap(frame, frameWidth, frameHeight, frameStride)[captureZone.Region.X, captureZone.Region.Y, captureZone.Region.Width, captureZone.Region.Height];
        Span<ColorBGRA> target = MemoryMarshal.Cast<byte, ColorBGRA>(buffer);

        int blockSize = 1 << captureZone.DownscaleLevel;

        int width = captureZone.Width;
        int height = captureZone.Height;

        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                target[(y * width) + x] = source[x * blockSize, y * blockSize, blockSize, blockSize].Average();
    }

    /// <inheritdoc />
    protected override bool PerformRestart()
    {
        lock (_captureLock)
        {
            // Dispose releases everything under this lock after setting the flag: nothing created here may outlive it.
            if (_isDisposed) return false;

            DisposeDX();

            try
            {
                // Found by its id and read again: after a mode change the display has another size,
                // and after displays were added or removed its adapter can have another index.
                Display display = DX9Devices.FindDisplay(_direct3D9, Display.Id) ?? throw new InvalidOperationException($"The display '{Display.Id}' isn't connected.");

                PresentParameters presentParameters = new()
                {
                    BackBufferWidth = display.Width,
                    BackBufferHeight = display.Height,
                    Windowed = true,
                    SwapEffect = SwapEffect.Discard
                };
                _device = _direct3D9.CreateDevice(display.Index, DeviceType.Hardware, IntPtr.Zero, CreateFlags.SoftwareVertexProcessing, presentParameters);
                _surface = _device.CreateOffscreenPlainSurface(display.Width, display.Height, Format.A8R8G8B8, Pool.Scratch);
                _frame = new FrameBuffer(display.Width, display.Height);

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
        try
        {
            try { _surface?.Dispose(); } catch { /**/}
            try { _device?.Dispose(); } catch { /**/}
            _frame = null;
            _device = null;
            _surface = null;
        }
        catch { /**/ }
    }

    #endregion

    private sealed class FrameBuffer(int width, int height)
    {
        public int Width { get; } = width;
        public int Height { get; } = height;
        public int Stride { get; } = width * ColorBGRA.ColorFormat.BytesPerPixel;
        public byte[] Data { get; } = new byte[height * width * ColorBGRA.ColorFormat.BytesPerPixel];

        // False until the first frame was read into Data.
        public bool HasData { get; set; }
    }
}
