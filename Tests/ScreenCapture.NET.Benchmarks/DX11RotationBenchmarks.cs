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

    private int _width;
    private int _height;
    private int _sourceStride;
    private byte[] _source = [];
    private byte[] _target = [];

    #endregion

    #region Methods

    [GlobalSetup]
    public void Setup()
    {
        (_width, _height) = Size switch
        {
            ZoneSize.FullHD => (1920, 1080),
            ZoneSize.SuperUltraWide => (5120, 1440),
            ZoneSize.Downscaled => (240, 135),
            _ => throw new ArgumentOutOfRangeException(nameof(Size))
        };

        // For 90/270 the texture is in the native orientation: one row per zone column.
        bool swapped = Rotation is ImageRotation.Rotate90 or ImageRotation.Rotate270;
        int sourceRows = swapped ? _width : _height;
        int sourceRowPixels = swapped ? _height : _width;

        // Staging textures have a padded row pitch; 256 bytes is a typical alignment.
        _sourceStride = BenchmarkHelper.Align(sourceRowPixels * 4, 256);
        _source = BenchmarkHelper.CreateFrame(sourceRowPixels, sourceRows, _sourceStride);
        _target = new byte[_width * _height * 4];
    }

    [Benchmark]
    public void CopyRotate() => CopyRotateHelper.Copy(Rotation, _source, _sourceStride, _width, _height, _target);

    #endregion
}
