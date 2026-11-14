using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using HPPH;

namespace ScreenCapture.NET.Benchmarks;

/// <summary>
/// <c>RemoveBlackBars</c> on all three overloads.
/// FullHD contains a letterboxed 2.39:1 picture (black bars top and bottom),
/// DualWQHD a pillarboxed 16:9 picture (black bars left and right).
/// </summary>
[SimpleJob(RuntimeMoniker.Net10_0)]
[HtmlExporter]
[MemoryDiagnoser]
public class BlackBarBenchmarks
{
    #region Properties & Fields

    private const int THRESHOLD = 5;

    [Params(Resolution.FullHD, Resolution.DualWQHD)]
    public Resolution Resolution { get; set; }

    private Image<ColorBGRA>? _image;

    #endregion

    #region Methods

    [GlobalSetup]
    public void Setup()
    {
        (int width, int height) = BenchmarkHelper.GetSize(Resolution);

        (int contentWidth, int contentHeight) = Resolution == Resolution.FullHD
                                                    ? (width, (int)(width / 2.39))
                                                    : ((height * 16) / 9, height);

        byte[] content = BenchmarkHelper.CreateFrame(contentWidth, contentHeight, contentWidth * 4);
        byte[] frame = new byte[width * height * 4];
        int offsetX = (width - contentWidth) / 2;
        int offsetY = (height - contentHeight) / 2;
        for (int y = 0; y < contentHeight; y++)
            content.AsSpan(y * contentWidth * 4, contentWidth * 4).CopyTo(frame.AsSpan((((offsetY + y) * width) + offsetX) * 4));

        _image = Image<ColorBGRA>.Wrap(frame, width, height, width * 4);
    }

    [Benchmark]
    public int RefImage()
    {
        RefImage<ColorBGRA> result = _image!.AsRefImage().RemoveBlackBars(THRESHOLD);
        return result.Width + result.Height;
    }

    [Benchmark]
    public int Image()
    {
        RefImage<ColorBGRA> result = _image!.RemoveBlackBars(THRESHOLD);
        return result.Width + result.Height;
    }

    [Benchmark]
    public int IImage()
    {
        IImage result = ((IImage)_image!).RemoveBlackBars(THRESHOLD);
        return result.Width + result.Height;
    }

    #endregion
}
