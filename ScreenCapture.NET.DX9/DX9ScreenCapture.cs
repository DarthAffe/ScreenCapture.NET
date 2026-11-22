using System;
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
/// A changed resolution makes the capture restart (see <see cref="IScreenCapture.DisplayChanged"/>).
/// </remarks>
// ReSharper disable once InconsistentNaming
public sealed class DX9ScreenCapture : AbstractScreenCapture<ColorBGRA>
{
    #region Properties & Fields

    private readonly Lock _captureLock = new();

    private readonly IDirect3D9Ex _direct3D9;
    private IDirect3DDevice9? _device;
    private IDirect3DSurface9? _surface;

    // Only used under _captureLock.
    private FrameBuffer? _frame;

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
        // DX9 doesn't tell when the frame was presented.
        timestamp = 0;

        lock (_captureLock)
        {
            FrameBuffer? frame = _frame;
            if ((_device == null) || (_surface == null) || (frame == null))
                return RestartCapture();

            // GetFrontBufferData needs a surface of the current display mode.
            if (!IsDisplayModeOf(frame))
                return RestartCapture();

            try
            {
                _device.GetFrontBufferData(0, _surface);

                LockedRectangle dr = _surface.LockRect(LockFlags.NoSystemLock | LockFlags.ReadOnly);
                try
                {
                    CopySurface(dr, frame);
                }
                finally
                {
                    _surface.UnlockRect();
                }
                frame.HasData = true;

                // No change detection: every read counts as a new frame.
                return CaptureResult.NewFrame;
            }
            catch (SharpGenException dxException)
            {
                // E.g. a UAC prompt, or a lost device after a GPU reset. A new device is cheap.
                ReportError(dxException);
                return RestartCapture();
            }
        }
    }

    private bool IsDisplayModeOf(FrameBuffer frame)
    {
        try
        {
            DisplayMode displayMode = _direct3D9.GetAdapterDisplayMode((uint)Display.Index);
            return (displayMode.Width == frame.Width) && (displayMode.Height == frame.Height);
        }
        catch (SharpGenException)
        {
            // E.g. the adapter is gone. The restart finds out.
            return false;
        }
    }

    /// <inheritdoc />
    protected override bool PerformCaptureZoneUpdate(CaptureZone<ColorBGRA> captureZone, Span<ColorBGRA> buffer)
    {
        ArgumentNullException.ThrowIfNull(captureZone);

        // A concurrent capture could overwrite the frame while it's copied.
        lock (_captureLock)
        {
            // A restart on another thread can have replaced the frame.
            FrameBuffer? frame = _frame;
            if ((frame == null) || !frame.HasData || !captureZone.Region.IsWithin(frame.Width, frame.Height)) return false;

            CaptureZoneWriter.Write(RefImage<ColorBGRA>.Wrap(frame.Data, frame.Width, frame.Height), captureZone, buffer);

            return true;
        }
    }

    // The last row may end without padding.
    private static unsafe void CopySurface(LockedRectangle surface, FrameBuffer frame)
    {
        ReadOnlySpan<byte> data = new((void*)surface.DataPointer, (surface.Pitch * (frame.Height - 1)) + (frame.Width * ColorBGRA.ColorFormat.BytesPerPixel));
        RefImage<ColorBGRA>.Wrap(data, frame.Width, frame.Height, surface.Pitch).CopyTo(frame.Data);
    }

    /// <inheritdoc />
    protected override bool PerformRestart()
    {
        lock (_captureLock)
        {
            // Nothing created here may outlive Dispose.
            if (IsDisposed) return false;

            DisposeDX();

            try
            {
                Display display = DX9Devices.FindDisplay(_direct3D9, Display.Id) ?? throw new InvalidOperationException($"The display '{Display.Id}' isn't connected.");

                PresentParameters presentParameters = new()
                {
                    BackBufferWidth = (uint)display.Width,
                    BackBufferHeight = (uint)display.Height,
                    Windowed = true,
                    SwapEffect = SwapEffect.Discard
                };
                _device = _direct3D9.CreateDevice((uint)display.Index, DeviceType.Hardware, IntPtr.Zero, CreateFlags.SoftwareVertexProcessing, presentParameters);
                _surface = _device.CreateOffscreenPlainSurface((uint)display.Width, (uint)display.Height, Format.A8R8G8B8, Pool.Scratch);
                _frame = new FrameBuffer(display.Width, display.Height);

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
        try { _surface?.Dispose(); } catch { /**/}
        try { _device?.Dispose(); } catch { /**/}
        _frame = null;
        _device = null;
        _surface = null;
    }

    #endregion

    private sealed class FrameBuffer(int width, int height)
    {
        public int Width { get; } = width;
        public int Height { get; } = height;
        public ColorBGRA[] Data { get; } = new ColorBGRA[width * height];

        public bool HasData { get; set; }
    }
}
