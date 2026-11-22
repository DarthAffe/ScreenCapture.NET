using System.Runtime.Versioning;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using HPPH;

namespace ScreenCapture.NET.Benchmarks;

/// <summary>
/// The CPU part of a DX11 zone update: copying the mapped staging texture into the zone buffer, with the display rotation applied.
/// </summary>
[SimpleJob(RuntimeMoniker.Net10_0)]
[HtmlExporter]
[MemoryDiagnoser]
[SupportedOSPlatform("windows")]
public class DX11RotationBenchmarks
{
    #region Properties & Fields

    public enum ZoneSize
    {
        /// <summary>1920x1080.</summary>
        FullHD,

        /// <summary>5120x1440.</summary>
        SuperUltraWide,

        /// <summary>240x135 (FullHD with downscale level 3).</summary>
        Downscaled
    }

    [Params(ZoneSize.FullHD, ZoneSize.SuperUltraWide, ZoneSize.Downscaled)]
    public ZoneSize Size { get; set; }

    [Params(ImageRotation.None, ImageRotation.Rotate90, ImageRotation.Rotate180, ImageRotation.Rotate270)]
    public ImageRotation Rotation { get; set; }

    private int _sourceWidth;
    private int _sourceHeight;
    private int _sourceStride;
    private byte[] _source = [];
    private ColorBGRA[] _target = [];

    private BenchmarkScreenCapture? _capture;
    private CaptureZone<ColorBGRA>? _zone;

    #endregion

    #region Methods

    [GlobalSetup]
    public void Setup()
    {
        (int width, int height) = Size switch
        {
            ZoneSize.FullHD => (1920, 1080),
            ZoneSize.SuperUltraWide => (5120, 1440),
            ZoneSize.Downscaled => (240, 135),
            _ => throw new ArgumentOutOfRangeException(nameof(Size))
        };

        // The texture is in the native orientation: for 90/270 one row per zone column.
        (_sourceWidth, _sourceHeight) = PixelHelper.GetRotatedSize(width, height, Rotation);

        // Staging textures have a padded row pitch; 256 bytes is a typical alignment.
        _sourceStride = BenchmarkHelper.Align(_sourceWidth * 4, 256);
        _source = BenchmarkHelper.CreateFrame(_sourceWidth, _sourceHeight, _sourceStride);

        _capture = new BenchmarkScreenCapture(width, height);
        _zone = _capture.RegisterCaptureZone(new CaptureRegion(0, 0, width, height));
        _target = new ColorBGRA[_zone.Width * _zone.Height];
    }

    [GlobalCleanup]
    public void Cleanup() => _capture?.Dispose();

    [Benchmark]
    public void CopyRotate() => CaptureZoneWriter.WriteFromScaled(RefImage<ColorBGRA>.Wrap(_source, _sourceWidth, _sourceHeight, _sourceStride), _zone!, _target, Rotation);

    #endregion
}
