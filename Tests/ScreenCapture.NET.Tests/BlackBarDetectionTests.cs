using System.Runtime.InteropServices;
using HPPH;

namespace ScreenCapture.NET.Tests;

[TestClass]
public sealed class BlackBarDetectionTests
{
    #region Constants

    private const int WIDTH = 10;
    private const int HEIGHT = 8;

    private static readonly ColorBGRA BLACK = new(0, 0, 0, 255);

    #endregion

    #region Helper

    public enum Overload { IImage, Image, RefImage }

    private static ColorBGRA Content(int x, int y) => new((byte)(10 + x), (byte)(10 + y), 200, 255);

    private static Image<ColorBGRA> CreateImage(params (int x, int y)[] content) => CreateImage(WIDTH, HEIGHT, content);

    private static Image<ColorBGRA> CreateImage(int width, int height, params (int x, int y)[] content)
    {
        ColorBGRA[] pixels = new ColorBGRA[width * height];
        Array.Fill(pixels, BLACK);
        foreach ((int x, int y) in content)
            pixels[(y * width) + x] = Content(x % 200, y % 200);

        return Image<ColorBGRA>.Create(pixels, width, height);
    }

    private static Image<ColorBGRA> CreateImageWithPixel(int x, int y, ColorBGRA color)
    {
        ColorBGRA[] pixels = new ColorBGRA[WIDTH * HEIGHT];
        Array.Fill(pixels, BLACK);
        pixels[(y * WIDTH) + x] = color;

        return Image<ColorBGRA>.Create(pixels, WIDTH, HEIGHT);
    }

    private static Image<ColorBGRA> Remove(Overload overload, Image<ColorBGRA> image, int threshold = 0,
                                           bool removeTop = true, bool removeBottom = true, bool removeLeft = true, bool removeRight = true)
        => overload switch
        {
            Overload.IImage => (Image<ColorBGRA>)((IImage)image).RemoveBlackBars(threshold, removeTop, removeBottom, removeLeft, removeRight).ToImage(),
            Overload.Image => image.RemoveBlackBars(threshold, removeTop, removeBottom, removeLeft, removeRight).ToImage(),
            Overload.RefImage => image.AsRefImage().RemoveBlackBars(threshold, removeTop, removeBottom, removeLeft, removeRight).ToImage(),
            _ => throw new ArgumentOutOfRangeException(nameof(overload))
        };

    private static (int top, int bottom, int left, int right) Calculate(Overload overload, Image<ColorBGRA> image, int threshold = 0)
        => overload switch
        {
            Overload.IImage => (((IImage)image).CalculateTop(threshold), ((IImage)image).CalculateBottom(threshold),
                                ((IImage)image).CalculateLeft(threshold), ((IImage)image).CalculateRight(threshold)),
            Overload.Image => (image.CalculateTop(threshold), image.CalculateBottom(threshold),
                               image.CalculateLeft(threshold), image.CalculateRight(threshold)),
            Overload.RefImage => (image.AsRefImage().CalculateTop(threshold), image.AsRefImage().CalculateBottom(threshold),
                                  image.AsRefImage().CalculateLeft(threshold), image.AsRefImage().CalculateRight(threshold)),
            _ => throw new ArgumentOutOfRangeException(nameof(overload))
        };

    private static void AssertRegion(Image<ColorBGRA> image, Image<ColorBGRA> result, int x, int y, int width, int height)
    {
        Assert.AreEqual(width, result.Width, "Width");
        Assert.AreEqual(height, result.Height, "Height");
        Assert.IsTrue(result.ContentEquals(image.Slice(x, y, width, height)), "Content");
    }

    #endregion

    #region Tests

    [TestMethod]
    [DataRow(Overload.IImage)]
    [DataRow(Overload.Image)]
    [DataRow(Overload.RefImage)]
    public void CropsToContentIncludingLastRowAndColumn(Overload overload)
    {
        Image<ColorBGRA> image = CreateImage((2, 3), (6, 5));

        AssertRegion(image, Remove(overload, image), 2, 3, 5, 3);
    }

    [TestMethod]
    [DataRow(Overload.IImage)]
    [DataRow(Overload.Image)]
    [DataRow(Overload.RefImage)]
    public void CalculatesHalfOpenBounds(Overload overload)
    {
        Image<ColorBGRA> image = CreateImage((2, 3), (6, 5));

        Assert.AreEqual((3, 6, 2, 7), Calculate(overload, image));
    }

    [TestMethod]
    [DataRow(Overload.IImage)]
    [DataRow(Overload.Image)]
    [DataRow(Overload.RefImage)]
    public void AllBlackReturnsWholeImage(Overload overload)
    {
        Image<ColorBGRA> image = CreateImage();

        AssertRegion(image, Remove(overload, image), 0, 0, WIDTH, HEIGHT);
    }

    [TestMethod]
    [DataRow(Overload.IImage)]
    [DataRow(Overload.Image)]
    [DataRow(Overload.RefImage)]
    public void AllBlackCalculatesNoBars(Overload overload)
    {
        Image<ColorBGRA> image = CreateImage();

        Assert.AreEqual((0, HEIGHT, 0, WIDTH), Calculate(overload, image));
    }

    [TestMethod]
    [DataRow(Overload.IImage)]
    [DataRow(Overload.Image)]
    [DataRow(Overload.RefImage)]
    public void NoBarsReturnsWholeImage(Overload overload)
    {
        Image<ColorBGRA> image = CreateImage((0, 0), (WIDTH - 1, HEIGHT - 1));

        AssertRegion(image, Remove(overload, image), 0, 0, WIDTH, HEIGHT);
    }

    [TestMethod]
    [DataRow(Overload.IImage, 0)]
    [DataRow(Overload.Image, 0)]
    [DataRow(Overload.RefImage, 0)]
    [DataRow(Overload.IImage, HEIGHT - 1)]
    [DataRow(Overload.Image, HEIGHT - 1)]
    [DataRow(Overload.RefImage, HEIGHT - 1)]
    public void SingleRowContent(Overload overload, int row)
    {
        Image<ColorBGRA> image = CreateImage([.. Enumerable.Range(0, WIDTH).Select(x => (x, row))]);

        AssertRegion(image, Remove(overload, image), 0, row, WIDTH, 1);
    }

    [TestMethod]
    [DataRow(Overload.IImage, 0)]
    [DataRow(Overload.Image, 0)]
    [DataRow(Overload.RefImage, 0)]
    [DataRow(Overload.IImage, WIDTH - 1)]
    [DataRow(Overload.Image, WIDTH - 1)]
    [DataRow(Overload.RefImage, WIDTH - 1)]
    public void SingleColumnContent(Overload overload, int column)
    {
        Image<ColorBGRA> image = CreateImage([.. Enumerable.Range(0, HEIGHT).Select(y => (column, y))]);

        AssertRegion(image, Remove(overload, image), column, 0, 1, HEIGHT);
    }

    // The columns are checked in strips of 256 pixels first, then narrowed down in strips of 64, 16, 4 and 1 pixels.
    // 600 = 2 * 256 + 88, so the strips seen from the left ([0, 256), [256, 512), [512, 600)) and from the right
    // ([344, 600), [88, 344), [0, 88)) differ. The columns sit on the edges of strips of every width, from both sides.
    // The strips are checked in blocks of 32 rows: the pixel is in a middle block (row 40) or in the last, partial block (row 66).
    [TestMethod]
    [DataRow(0)]
    [DataRow(87)]
    [DataRow(88)]
    [DataRow(255)]
    [DataRow(256)]
    [DataRow(259)]
    [DataRow(260)]
    [DataRow(271)]
    [DataRow(272)]
    [DataRow(319)]
    [DataRow(320)]
    [DataRow(343)]
    [DataRow(344)]
    [DataRow(511)]
    [DataRow(512)]
    [DataRow(535)]
    [DataRow(536)]
    [DataRow(583)]
    [DataRow(584)]
    [DataRow(595)]
    [DataRow(596)]
    [DataRow(599)]
    public void FindsColumnsAcrossStrips(int column)
    {
        const int width = 600;
        const int height = 70;

        foreach (int row in (int[])[40, 66])
        {
            Image<ColorBGRA> image = CreateImage(width, height, (column, row));

            foreach (Overload overload in Enum.GetValues<Overload>())
            {
                string context = $"row {row}, {overload}";
                Image<ColorBGRA> result = Remove(overload, image);
                Assert.AreEqual(1, result.Width, $"{context}: Width");
                Assert.AreEqual(1, result.Height, $"{context}: Height");
                Assert.IsTrue(result.ContentEquals(image.Slice(column, row, 1, 1)), $"{context}: Content");

                Assert.AreEqual((row, row + 1, column, column + 1), Calculate(overload, image), $"{context}: Bounds");
            }
        }
    }

    [TestMethod]
    public void MatchesBruteForceReferenceOnRandomImages()
    {
        Random random = new(4711);
        for (int i = 0; i < 500; i++)
        {
            // Up to 2 strips of 256 px and 2 blocks of 32 rows.
            int width = random.Next(1, 700);
            int height = random.Next(1, 80);
            int threshold = random.Next(0, 10);

            // A few pixels near the threshold, so some of them count as black.
            ColorBGRA[] pixels = new ColorBGRA[width * height];
            Array.Fill(pixels, BLACK);
            int count = random.Next(0, 4);
            for (int p = 0; p < count; p++)
            {
                int value = threshold + random.Next(-1, 3);
                byte channel = (byte)Math.Clamp(value, 0, 255);
                pixels[random.Next(pixels.Length)] = random.Next(3) switch
                {
                    0 => new ColorBGRA(channel, 0, 0, 255),
                    1 => new ColorBGRA(0, channel, 0, 255),
                    _ => new ColorBGRA(0, 0, channel, 255)
                };
            }

            Image<ColorBGRA> image = Image<ColorBGRA>.Create(pixels, width, height);
            (int top, int bottom, int left, int right) expected = ReferenceBounds(pixels, width, height, threshold);
            string context = $"#{i}: {width}x{height}, threshold {threshold}";

            foreach (Overload overload in Enum.GetValues<Overload>())
            {
                Assert.AreEqual(expected, Calculate(overload, image, threshold), $"{context}, {overload}: Calculate*");
                AssertRegion(image, Remove(overload, image, threshold), expected.left, expected.top, expected.right - expected.left, expected.bottom - expected.top);
            }
        }
    }

    /// <summary>
    /// The half-open content bounds, checked pixel by pixel. A completely black image gives the whole image.
    /// </summary>
    private static (int top, int bottom, int left, int right) ReferenceBounds(ColorBGRA[] pixels, int width, int height, int threshold)
    {
        int top = int.MaxValue, bottom = int.MinValue, left = int.MaxValue, right = int.MinValue;
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                ColorBGRA color = pixels[(y * width) + x];
                if ((color.R <= threshold) && (color.G <= threshold) && (color.B <= threshold)) continue;

                top = Math.Min(top, y);
                bottom = Math.Max(bottom, y + 1);
                left = Math.Min(left, x);
                right = Math.Max(right, x + 1);
            }

        return top == int.MaxValue ? (0, height, 0, width) : (top, bottom, left, right);
    }

    [TestMethod]
    [DataRow(Overload.IImage)]
    [DataRow(Overload.Image)]
    [DataRow(Overload.RefImage)]
    public void FindsBarsOnAllSidesOfWideImage(Overload overload)
    {
        // Letterbox and pillarbox at once: content spans x 70..129 and y 4..25.
        Image<ColorBGRA> image = CreateImage(200, 30, (70, 4), (129, 25), (100, 15));

        AssertRegion(image, Remove(overload, image), 70, 4, 60, 22);
    }

    [TestMethod]
    public void ImageOverloadSharesTheBuffer()
    {
        ColorBGRA[] pixels = new ColorBGRA[WIDTH * HEIGHT];
        Array.Fill(pixels, BLACK);
        pixels[(3 * WIDTH) + 2] = Content(2, 3);
        byte[] buffer = MemoryMarshal.AsBytes(pixels.AsSpan()).ToArray();
        Image<ColorBGRA> image = Image<ColorBGRA>.Wrap(buffer, WIDTH, HEIGHT, WIDTH * 4);

        Image<ColorBGRA> result = image.RemoveBlackBars();

        // Changing the buffer afterwards shows up in the result: it's a view, not a copy.
        buffer[((3 * WIDTH) + 2) * 4] = 0x99;
        Assert.AreEqual(1, result.Width);
        Assert.AreEqual(1, result.Height);
        Assert.AreEqual(0x99, result[0, 0].B);
    }

    [TestMethod]
    [DataRow(Overload.IImage)]
    [DataRow(Overload.Image)]
    [DataRow(Overload.RefImage)]
    public void SinglePixelInBottomRightCorner(Overload overload)
    {
        Image<ColorBGRA> image = CreateImage((WIDTH - 1, HEIGHT - 1));

        AssertRegion(image, Remove(overload, image), WIDTH - 1, HEIGHT - 1, 1, 1);
    }

    [TestMethod]
    [DataRow(Overload.IImage, (byte)5, (byte)5, (byte)5, false)]
    [DataRow(Overload.Image, (byte)5, (byte)5, (byte)5, false)]
    [DataRow(Overload.RefImage, (byte)5, (byte)5, (byte)5, false)]
    [DataRow(Overload.IImage, (byte)6, (byte)0, (byte)0, true)]
    [DataRow(Overload.Image, (byte)0, (byte)6, (byte)0, true)]
    [DataRow(Overload.RefImage, (byte)0, (byte)0, (byte)6, true)]
    public void ThresholdIsInclusive(Overload overload, byte r, byte g, byte b, bool isContent)
    {
        Image<ColorBGRA> image = CreateImageWithPixel(3, 4, new ColorBGRA(b, g, r, 255));

        Image<ColorBGRA> result = Remove(overload, image, threshold: 5);

        if (isContent)
            AssertRegion(image, result, 3, 4, 1, 1);
        else
            AssertRegion(image, result, 0, 0, WIDTH, HEIGHT);
    }

    [TestMethod]
    [DataRow(Overload.IImage)]
    [DataRow(Overload.Image)]
    [DataRow(Overload.RefImage)]
    public void RespectsRemoveFlags(Overload overload)
    {
        // Content spans x 2..6 and y 3..5.
        Image<ColorBGRA> image = CreateImage((2, 3), (6, 5));

        AssertRegion(image, Remove(overload, image, removeTop: false), 2, 0, 5, 6);
        AssertRegion(image, Remove(overload, image, removeBottom: false), 2, 3, 5, HEIGHT - 3);
        AssertRegion(image, Remove(overload, image, removeLeft: false), 0, 3, 7, 3);
        AssertRegion(image, Remove(overload, image, removeRight: false), 2, 3, WIDTH - 2, 3);
        AssertRegion(image, Remove(overload, image, removeTop: false, removeBottom: false, removeLeft: false, removeRight: false), 0, 0, WIDTH, HEIGHT);
    }

    #endregion
}
