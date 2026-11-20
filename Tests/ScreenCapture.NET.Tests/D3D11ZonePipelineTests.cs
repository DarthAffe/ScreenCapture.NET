using System.Runtime.Versioning;
using HPPH;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace ScreenCapture.NET.Tests;

/// <summary>
/// <see cref="D3D11ZonePipeline"/>: zones written from frames on the GPU, run on WARP (Direct3D's software rasterizer, available on every Windows, also without a GPU).
/// The reference is the CPU zone write of <see cref="CaptureZoneWriter"/> from the same frame.
/// </summary>
[TestClass]
[OSCondition(OperatingSystems.Windows)]
[SupportedOSPlatform("windows")]
public sealed class D3D11ZonePipelineTests
{
    #region Constants

    // Sizes divisible by 8: downscaled zones only use whole blocks, so the mip chain averages the same pixels as the CPU.
    private const int DISPLAY_WIDTH = 40;
    private const int DISPLAY_HEIGHT = 24;

    // The mip chain rounds on every level, the CPU only once.
    private const int MAX_MIP_DIFFERENCE = 1;

    #endregion

    #region Methods

    private static Display CreateDisplay(int width, int height, ImageRotation rotation)
        => TestScreenCapture.CreateDisplay(width, height, rotation);

    // Random pixels in the native orientation of the output (the display rotated back): a pixel read from the wrong place shows.
    private static ColorBGRA[] CreateFrame(Display display, int seed)
    {
        (int width, int height) = PixelHelper.GetRotatedSize(display.Width, display.Height, display.Rotation);

        Random random = new(seed);
        ColorBGRA[] pixels = new ColorBGRA[width * height];
        for (int i = 0; i < pixels.Length; i++)
            pixels[i] = new ColorBGRA((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256));

        return pixels;
    }

    private static ColorBGRA[] GetPixels(CaptureZone<ColorBGRA> zone)
    {
        using CaptureFrame<ColorBGRA> frame = zone.AcquireFrame();
        return frame.Pixels.ToArray();
    }

    private static void AssertZone(ColorBGRA[] frame, Display display, CaptureZone<ColorBGRA> zone, string message)
    {
        ColorBGRA[] expected = WarpScreenCapture.WriteReference(frame, display, zone);
        ColorBGRA[] actual = GetPixels(zone);
        Assert.HasCount(expected.Length, actual, message);

        int maxDifference = zone.DownscaleLevel == 0 ? 0 : MAX_MIP_DIFFERENCE;
        for (int i = 0; i < expected.Length; i++)
        {
            int difference = Math.Max(Math.Max(Math.Abs(expected[i].R - actual[i].R), Math.Abs(expected[i].G - actual[i].G)),
                                      Math.Max(Math.Abs(expected[i].B - actual[i].B), Math.Abs(expected[i].A - actual[i].A)));
            Assert.IsLessThanOrEqualTo(maxDifference, difference, $"{message}: pixel {i % zone.Width}, {i / zone.Width} is {actual[i]}, expected {expected[i]}.");
        }
    }

    [TestMethod]
    [DataRow(ImageRotation.None)]
    [DataRow(ImageRotation.Rotate90)]
    [DataRow(ImageRotation.Rotate180)]
    [DataRow(ImageRotation.Rotate270)]
    public void ZonesShowTheirRegionOfTheFrame(ImageRotation rotation)
    {
        Display display = CreateDisplay(DISPLAY_WIDTH, DISPLAY_HEIGHT, rotation);
        using WarpScreenCapture capture = new(display);

        (CaptureRegion Region, int Level)[] layouts =
        [
            (new CaptureRegion(0, 0, DISPLAY_WIDTH, DISPLAY_HEIGHT), 0),
            (new CaptureRegion(0, 0, 1, 1), 0),
            (new CaptureRegion(DISPLAY_WIDTH - 1, DISPLAY_HEIGHT - 1, 1, 1), 0),
            (new CaptureRegion(3, 5, 17, 9), 0),
            (new CaptureRegion(DISPLAY_WIDTH - 7, 2, 7, 19), 0),
            (new CaptureRegion(3, 5, 16, 8), 1),
            (new CaptureRegion(1, 2, 12, 20), 2),
            (new CaptureRegion(0, 0, DISPLAY_WIDTH, DISPLAY_HEIGHT), 3),
            (new CaptureRegion(5, 3, 16, 16), 3)
        ];
        CaptureZone<ColorBGRA>[] zones = [.. layouts.Select(layout => capture.RegisterCaptureZone(layout.Region, layout.Level))];

        ColorBGRA[] frame = CreateFrame(display, 1);
        capture.SetFrame(frame);
        Assert.AreEqual(CaptureResult.NewFrame, capture.CaptureScreen());

        foreach (CaptureZone<ColorBGRA> zone in zones)
            AssertZone(frame, display, zone, $"{zone.Region} L{zone.DownscaleLevel}");
    }

    [TestMethod]
    public void ZonesChangedAfterTheCopiesWereQueuedShowTheirNewRegion()
    {
        Display display = CreateDisplay(DISPLAY_WIDTH, DISPLAY_HEIGHT, ImageRotation.Rotate90);
        using WarpScreenCapture capture = new(display);

        CaptureZone<ColorBGRA> moved = capture.RegisterCaptureZone(new CaptureRegion(3, 5, 9, 7));
        CaptureZone<ColorBGRA> resized = capture.RegisterCaptureZone(new CaptureRegion(8, 0, 16, 8), 1);
        CaptureZone<ColorBGRA> rescaled = capture.RegisterCaptureZone(new CaptureRegion(0, 8, 16, 16), 1);

        // Written once, so the zones have textures and the next capture queues their copies.
        capture.SetFrame(CreateFrame(display, 1));
        Assert.AreEqual(CaptureResult.NewFrame, capture.CaptureScreen());

        // Like a zone changed on another thread while the capture waits for the GPU: its copy was queued for the old region.
        capture.CopiesQueued = () =>
        {
            capture.UpdateCaptureZone(moved, moved.Region with { X = 20, Y = 11 });
            capture.UpdateCaptureZone(resized, resized.Region with { Width = 8, Height = 16 });
            capture.UpdateCaptureZone(rescaled, downscaleLevel: 2);
        };

        ColorBGRA[] frame = CreateFrame(display, 2);
        capture.SetFrame(frame);
        Assert.AreEqual(CaptureResult.NewFrame, capture.CaptureScreen());

        AssertZone(frame, display, moved, "moved");
        AssertZone(frame, display, resized, "resized");
        AssertZone(frame, display, rescaled, "rescaled");
    }

    [TestMethod]
    public void RequestedUpdateShowsTheCurrentFrame()
    {
        Display display = CreateDisplay(DISPLAY_WIDTH, DISPLAY_HEIGHT, ImageRotation.None);
        using WarpScreenCapture capture = new(display);
        CaptureZone<ColorBGRA> zone = capture.RegisterCaptureZone(new CaptureRegion(3, 5, 9, 7));

        capture.SetFrame(CreateFrame(display, 3));
        capture.CaptureScreen();

        // Not copied with the next frame (not updated automatically), but requested before the zones are written:
        // its staging texture still holds the same region of the previous frame.
        zone.AutoUpdate = false;
        capture.CopiesQueued = zone.RequestUpdate;

        ColorBGRA[] frame = CreateFrame(display, 4);
        capture.SetFrame(frame);
        Assert.AreEqual(CaptureResult.NewFrame, capture.CaptureScreen());

        AssertZone(frame, display, zone, "requested");
    }

    [TestMethod]
    public void ReinitializedPipelineUsesTheNewRotation()
    {
        // Square: the zone keeps its size, only the orientation of the frame (and so of its textures) changes.
        Display display = CreateDisplay(32, 32, ImageRotation.None);
        using WarpScreenCapture capture = new(display);
        CaptureZone<ColorBGRA> zone = capture.RegisterCaptureZone(new CaptureRegion(3, 5, 10, 6));
        CaptureZone<ColorBGRA> downscaled = capture.RegisterCaptureZone(new CaptureRegion(8, 0, 16, 8), 1);

        capture.SetFrame(CreateFrame(display, 5));
        capture.CaptureScreen();

        Display rotated = display with { Rotation = ImageRotation.Rotate90 };
        capture.Reinitialize(rotated);

        ColorBGRA[] frame = CreateFrame(rotated, 6);
        capture.SetFrame(frame);
        Assert.AreEqual(CaptureResult.NewFrame, capture.CaptureScreen());

        AssertZone(frame, rotated, zone, "zone");
        AssertZone(frame, rotated, downscaled, "downscaled");
    }

    [TestMethod]
    public void HdrFramesAreToneMapped()
    {
        Display display = CreateDisplay(16, 16, ImageRotation.None);
        using WarpScreenCapture capture = new(display);
        CaptureZone<ColorBGRA> zone = capture.RegisterCaptureZone(new CaptureRegion(0, 0, 16, 16));

        // SDR colors as Windows shows them on an HDR display with SDR white at 200 nits (1.0 = 80 nits); the pixel shader itself is covered by HdrToneMappingTests.
        const float SDR_WHITE_LEVEL = 200;
        ColorBGRA[] colors = [.. Enumerable.Range(0, 256).Select(i => new ColorBGRA((byte)((i * 7) % 256), (byte)(255 - i), (byte)i, 255))];
        float scale = SDR_WHITE_LEVEL / HdrToneMapper.SCRGB_WHITE_NITS;

        capture.SetHdrFrame(ToHdr(colors), SDR_WHITE_LEVEL);
        Assert.AreEqual(CaptureResult.NewFrame, capture.CaptureScreen());
        CollectionAssert.AreEqual(colors, GetPixels(zone), "first frame");

        // Without a white level (the frame goes through SetFrame like in the captures), the last one is kept. Other colors, so a frame that isn't tone mapped is detected.
        ColorBGRA[] otherColors = [.. Enumerable.Reverse(colors)];
        capture.SetHdrFrame(ToHdr(otherColors), null);
        Assert.AreEqual(CaptureResult.NewFrame, capture.CaptureScreen());
        CollectionAssert.AreEqual(otherColors, GetPixels(zone), "white level kept");

        Half[] ToHdr(ColorBGRA[] sdr) => [.. sdr.SelectMany(c => new[] { (Half)(HdrToneMappingTests.SrgbToLinear(c.R) * scale), (Half)(HdrToneMappingTests.SrgbToLinear(c.G) * scale),
                                                                         (Half)(HdrToneMappingTests.SrgbToLinear(c.B) * scale), (Half)1 })];
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void FramesLargerThanTheDisplayUseTheirTopLeft(bool hdr)
    {
        // Like the texture of a WGC frame pool, which can be larger than its content.
        const int FRAME_WIDTH = 21;
        const int FRAME_HEIGHT = 19;
        Display display = CreateDisplay(16, 16, ImageRotation.None);
        using WarpScreenCapture capture = new(display);
        CaptureZone<ColorBGRA> zone = capture.RegisterCaptureZone(new CaptureRegion(0, 0, 16, 16));

        Random random = new(7);
        ColorBGRA[] frame = [.. Enumerable.Range(0, FRAME_WIDTH * FRAME_HEIGHT).Select(_ => new ColorBGRA((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256), 255))];
        if (hdr)
            // 1.0 = SDR white (80 nits): no scaling.
            capture.SetHdrFrame([.. frame.SelectMany(c => new[] { (Half)HdrToneMappingTests.SrgbToLinear(c.R), (Half)HdrToneMappingTests.SrgbToLinear(c.G), (Half)HdrToneMappingTests.SrgbToLinear(c.B), (Half)1 })],
                                HdrToneMapper.SCRGB_WHITE_NITS, FRAME_WIDTH, FRAME_HEIGHT);
        else
            capture.SetFrame(frame, FRAME_WIDTH, FRAME_HEIGHT);

        Assert.AreEqual(CaptureResult.NewFrame, capture.CaptureScreen());

        ColorBGRA[] expected = [.. Enumerable.Range(0, 16 * 16).Select(i => frame[((i / 16) * FRAME_WIDTH) + (i % 16)])];
        CollectionAssert.AreEqual(expected, GetPixels(zone));
    }

    #endregion

    /// <summary>
    /// A capture of frames set by the test, written through a <see cref="D3D11ZonePipeline"/> on a WARP device (like <see cref="DX11ScreenCapture"/> without the duplication).
    /// </summary>
    private sealed class WarpScreenCapture : AbstractScreenCapture<ColorBGRA>
    {
        #region Properties & Fields

        private readonly ID3D11Device _device;
        private readonly ID3D11DeviceContext _context;
        private readonly D3D11ZonePipeline _zonePipeline;

        private ID3D11Texture2D? _nextFrame;
        private bool _nextFrameIsHdr;
        private float? _nextSdrWhiteLevel;

        /// <summary>
        /// Gets or sets an action run by every capture after the zone copies were queued and before the zones are written (without a lock held).
        /// </summary>
        public Action? CopiesQueued { get; set; }

        #endregion

        #region Constructors

        public WarpScreenCapture(Display display)
            : base(display)
        {
            D3D11.D3D11CreateDevice(null, DriverType.Warp, DeviceCreationFlags.None, [FeatureLevel.Level_10_0], out ID3D11Device? device).CheckError();
            _device = device!;
            _context = _device.ImmediateContext;

            _zonePipeline = new D3D11ZonePipeline();
            _zonePipeline.Initialize(_device, _context, display, display.Rotation);
        }

        #endregion

        #region Methods

        /// <summary>
        /// Sets the frame of the next capture, in the native orientation of the output.
        /// </summary>
        public void SetFrame(ColorBGRA[] pixels)
        {
            (int width, int height) = PixelHelper.GetRotatedSize(Display.Width, Display.Height, Display.Rotation);
            SetFrame(pixels, width, height);
        }

        /// <summary>
        /// Sets the frame of the next capture with its own size (at least the one of the output).
        /// </summary>
        public void SetFrame(ColorBGRA[] pixels, int width, int height)
            => SetNextFrame(CreateTexture(Format.B8G8R8A8_UNorm, width, height, pixels, width * 4), false, null);

        /// <summary>
        /// Sets an HDR frame (scRGB, RGBA) of the next capture, in the orientation of the display (only used without a rotation).
        /// </summary>
        public void SetHdrFrame(Half[] rgba, float? sdrWhiteLevel) => SetHdrFrame(rgba, sdrWhiteLevel, Display.Width, Display.Height);

        /// <summary>
        /// Sets an HDR frame (scRGB, RGBA) of the next capture with its own size (at least the one of the display).
        /// </summary>
        public void SetHdrFrame(Half[] rgba, float? sdrWhiteLevel, int width, int height)
            => SetNextFrame(CreateTexture(Format.R16G16B16A16_Float, width, height, rgba, width * 8), true, sdrWhiteLevel);

        private void SetNextFrame(ID3D11Texture2D frame, bool isHdr, float? sdrWhiteLevel)
        {
            _nextFrame?.Dispose();
            _nextFrame = frame;
            _nextFrameIsHdr = isHdr;
            _nextSdrWhiteLevel = sdrWhiteLevel;
        }

        private unsafe ID3D11Texture2D CreateTexture<T>(Format format, int width, int height, T[] data, int rowPitch)
            where T : unmanaged
        {
            fixed (T* pointer = data)
                return _device.CreateTexture2D(new Texture2DDescription(format, width, height, 1, 1, BindFlags.ShaderResource), new SubresourceData((nint)pointer, rowPitch));
        }

        /// <summary>
        /// Initializes the pipeline again for another state of the display, like a restart after a mode change.
        /// </summary>
        public void Reinitialize(Display display)
        {
            _zonePipeline.Initialize(_device, _context, display, display.Rotation);
            SetDisplay(display);
        }

        /// <summary>
        /// Writes a zone on the CPU from a frame in the native orientation of the output.
        /// </summary>
        public static ColorBGRA[] WriteReference(ColorBGRA[] frame, Display display, CaptureZone<ColorBGRA> zone)
        {
            (int width, int height) = PixelHelper.GetRotatedSize(display.Width, display.Height, display.Rotation);
            ColorBGRA[] buffer = new ColorBGRA[zone.Width * zone.Height];
            CaptureZoneWriter.Write(RefImage<ColorBGRA>.Wrap(frame, width, height), zone, buffer, display.Rotation);
            return buffer;
        }

        protected override CaptureResult PerformScreenCapture(out long timestamp)
        {
            timestamp = 0;

            CaptureResult result = CaptureResult.NoChange;
            if (_nextFrame != null)
            {
                // Like the captures (SetFrame picks by the format), unless the test sets a white level (there's no monitor to read it from).
                if (_nextFrameIsHdr && (_nextSdrWhiteLevel != null))
                    _zonePipeline.ToneMapFrame(_nextFrame, _nextSdrWhiteLevel);
                else
                    _zonePipeline.SetFrame(_nextFrame, null);

                // The queued copy keeps the texture alive until the GPU did it.
                _nextFrame.Dispose();
                _nextFrame = null;
                result = CaptureResult.NewFrame;
            }

            _zonePipeline.QueueZoneCopies(CaptureZones);
            CopiesQueued?.Invoke();

            return result;
        }

        protected override bool PerformCaptureZoneUpdate(CaptureZone<ColorBGRA> captureZone, Span<ColorBGRA> buffer) => _zonePipeline.WriteZone(captureZone, buffer);

        public override bool UnregisterCaptureZone(CaptureZone<ColorBGRA> captureZone)
        {
            if (!base.UnregisterCaptureZone(captureZone)) return false;

            _zonePipeline.RemoveZone(captureZone);
            return true;
        }

        protected override bool PerformRestart() => true;

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);

            _zonePipeline.Release();
            _nextFrame?.Dispose();
            _context.Dispose();
            _device.Dispose();
        }

        #endregion
    }
}
