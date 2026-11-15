using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using HPPH;

namespace ScreenCapture.NET.Benchmarks;

/// <summary>
/// <c>RemoveBlackBars</c> on all three overloads.
/// See <see cref="BlackBarScene"/> for the frames.
/// </summary>
[SimpleJob(RuntimeMoniker.Net10_0)]
[HtmlExporter]
[MemoryDiagnoser]
public class BlackBarBenchmarks
{
    #region Properties & Fields

    private const int THRESHOLD = 5;

    public enum BlackBarScene
    {
        /// <summary>1920x1080 with a 2.39:1 picture: black bars top and bottom.</summary>
        FullHDLetterbox,

        /// <summary>5120x1440 with a 16:9 picture: 1280 px black bars left and right.</summary>
        DualWQHDPillarbox,

        /// <summary>5120x1440 with a 2.37:1 (21:9) picture: 853 px black bars left and right, not a multiple of the 64 px column strips.</summary>
        DualWQHDCinema
    }

    [Params(BlackBarScene.FullHDLetterbox, BlackBarScene.DualWQHDPillarbox, BlackBarScene.DualWQHDCinema)]
    public BlackBarScene Scene { get; set; }

    private Image<ColorBGRA>? _image;

    #endregion

    #region Methods

    [GlobalSetup]
    public void Setup()
    {
        (int width, int height) = BenchmarkHelper.GetSize(Scene == BlackBarScene.FullHDLetterbox ? Resolution.FullHD : Resolution.DualWQHD);

        (int contentWidth, int contentHeight) = Scene switch
        {
            BlackBarScene.FullHDLetterbox => (width, (int)(width / 2.39)),
            BlackBarScene.DualWQHDPillarbox => ((height * 16) / 9, height),
            _ => ((int)(height * 2.37), height)
        };

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
        Image<ColorBGRA> result = _image!.RemoveBlackBars(THRESHOLD);
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
