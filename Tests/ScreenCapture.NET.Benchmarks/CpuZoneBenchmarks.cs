using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using HPPH;

namespace ScreenCapture.NET.Benchmarks;

/// <summary>
/// The CPU zone update used by DX9 and X11 (<c>AbstractScreenCapture.WriteCaptureZone</c>): a copy for downscale level 0, HPPH's <c>Downscale</c> otherwise.
/// With a rotation the frame is in the native orientation of the output and the result is rotated as well (downscaled first).
/// </summary>
[SimpleJob(RuntimeMoniker.Net10_0)]
[HtmlExporter]
[MemoryDiagnoser]
public class CpuZoneBenchmarks
{
    #region Properties & Fields

    [Params(Resolution.FullHD, Resolution.DualWQHD)]
    public Resolution Resolution { get; set; }

    [Params(0, 1, 3, 5)]
    public int DownscaleLevel { get; set; }

    [Params(ImageRotation.None, ImageRotation.Rotate90)]
    public ImageRotation Rotation { get; set; }

    private int _width;
    private int _height;
    private int _frameWidth;
    private int _frameHeight;
    private int _stride;
    private byte[] _frame = [];
    private byte[] _buffer = [];

    private BenchmarkScreenCapture? _capture;
    private CaptureZone<ColorBGRA>? _zone;

    #endregion

    #region Methods

    [GlobalSetup]
    public void Setup()
    {
        (_width, _height) = BenchmarkHelper.GetSize(Resolution);
        (_frameWidth, _frameHeight) = PixelHelper.GetRotatedSize(_width, _height, Rotation);
        _stride = _frameWidth * 4;
        _frame = BenchmarkHelper.CreateFrame(_frameWidth, _frameHeight, _stride);

        _capture = new BenchmarkScreenCapture(_width, _height);
        _zone = _capture.RegisterCaptureZone(new CaptureRegion(0, 0, _width, _height), DownscaleLevel);
        _buffer = new byte[_zone.Stride * _zone.Height];
    }

    [GlobalCleanup]
    public void Cleanup() => _capture?.Dispose();

    [Benchmark]
    public void UpdateFullScreenZone() => BenchmarkScreenCapture.Write(RefImage<ColorBGRA>.Wrap(_frame, _frameWidth, _frameHeight, _stride), _zone!, _buffer, Rotation);

    #endregion
}
