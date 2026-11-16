using System.Runtime.Versioning;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using HPPH;

namespace ScreenCapture.NET.Benchmarks;

/// <summary>
/// The CPU zone update used by DX9 (X11 has an identical copy): <c>CopyZone</c> for downscale level 0, <c>DownscaleZone</c> otherwise.
/// </summary>
[SimpleJob(RuntimeMoniker.Net10_0)]
[HtmlExporter]
[MemoryDiagnoser]
[SupportedOSPlatform("windows")]
public class CpuZoneBenchmarks
{
    #region Properties & Fields

    [Params(Resolution.FullHD, Resolution.DualWQHD)]
    public Resolution Resolution { get; set; }

    [Params(0, 1, 3, 5)]
    public int DownscaleLevel { get; set; }

    private int _width;
    private int _height;
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
        _stride = _width * 4;
        _frame = BenchmarkHelper.CreateFrame(_width, _height, _stride);

        _capture = new BenchmarkScreenCapture(_width, _height);
        _zone = _capture.RegisterCaptureZone(new CaptureRegion(0, 0, _width, _height), DownscaleLevel);
        _buffer = new byte[_zone.Stride * _zone.Height];
    }

    [GlobalCleanup]
    public void Cleanup() => _capture?.Dispose();

    [Benchmark]
    public void UpdateFullScreenZone()
    {
        if (_zone!.DownscaleLevel == 0)
            DX9ScreenCapture.CopyZone(_frame, _width, _height, _stride, _zone, _buffer);
        else
            DX9ScreenCapture.DownscaleZone(_frame, _width, _height, _stride, _zone, _buffer);
    }

    #endregion
}
