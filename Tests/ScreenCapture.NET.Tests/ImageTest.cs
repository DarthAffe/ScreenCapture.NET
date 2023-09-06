using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ScreenCapture.NET.Tests;

[TestClass]
public class ImageTest
{
    #region Properties & Fields

    private static IScreenCapture? _screenCapture;
    private static ICaptureZone? _captureZone;

    #endregion

    #region Methods

    [ClassInitialize]
    public static void ClassInit(TestContext _)
    {
        _screenCapture = new TestScreenCapture();
        _captureZone = _screenCapture.RegisterCaptureZone(0, 0, _screenCapture.Display.Width, _screenCapture.Display.Height);
        _screenCapture.CaptureScreen();
    }

    [ClassCleanup]
    public static void ClassCleanup()
    {
        _screenCapture?.Dispose();
        _screenCapture = null;
    }

    [TestMethod]
    public void TestImageFullScreen()
    {
        IImage image = _captureZone!.Image;

        Assert.AreEqual(_captureZone.Width, image.Width);
        Assert.AreEqual(_captureZone.Height, image.Height);

        for (int y = 0; y < image.Height; y++)
            for (int x = 0; x < image.Width; x++)
                Assert.AreEqual(TestScreenCapture.GetColorFromLocation(x, y), image[x, y]);
    }

    [TestMethod]
    public void TestImageInnerFull()
    {
        IImage image = _captureZone!.Image;
        image = image[0, 0, image.Width, image.Height];

        for (int y = 0; y < image.Height; y++)
            for (int x = 0; x < image.Width; x++)
                Assert.AreEqual(TestScreenCapture.GetColorFromLocation(x, y), image[x, y]);
    }

    [TestMethod]
    public void TestImageEnumerator()
    {
        IImage image = _captureZone!.Image;

        int counter = 0;
        foreach (IColor color in image)
        {
            int x = counter % image.Width;
            int y = counter / image.Width;

            Assert.AreEqual(TestScreenCapture.GetColorFromLocation(x, y), color);

            counter++;
        }
    }

    [TestMethod]
    public void TestImageInnerPartial()
    {
        IImage image = _captureZone!.Image;
        image = image[163, 280, 720, 13];

        Assert.AreEqual(720, image.Width);
        Assert.AreEqual(13, image.Height);

        for (int y = 0; y < image.Height; y++)
            for (int x = 0; x < image.Width; x++)
                Assert.AreEqual(TestScreenCapture.GetColorFromLocation(163 + x, 280 + y), image[x, y]);
    }

    [TestMethod]
    public void TestImageInnerInnerPartial()
    {
        IImage image = _captureZone!.Image;
        image = image[163, 280, 720, 13];
        image = image[15, 2, 47, 8];

        Assert.AreEqual(47, image.Width);
        Assert.AreEqual(8, image.Height);

        for (int y = 0; y < image.Height; y++)
            for (int x = 0; x < image.Width; x++)
                Assert.AreEqual(TestScreenCapture.GetColorFromLocation(178 + x, 282 + y), image[x, y]);
    }

    [TestMethod]
    public void TestImageRowIndexer()
    {
        IImage image = _captureZone!.Image;

        Assert.AreEqual(image.Height, image.Rows.Count);

        for (int y = 0; y < image.Height; y++)
        {
            IImage.IImageRow row = image.Rows[y];
            Assert.AreEqual(image.Width, row.Length);
            for (int x = 0; x < row.Length; x++)
                Assert.AreEqual(TestScreenCapture.GetColorFromLocation(x, y), row[x]);
        }
    }

    [TestMethod]
    public void TestImageRowEnumerator()
    {
        IImage image = _captureZone!.Image;

        int y = 0;
        foreach (IImage.IImageRow row in image.Rows)
        {
            for (int x = 0; x < row.Length; x++)
                Assert.AreEqual(TestScreenCapture.GetColorFromLocation(x, y), row[x]);

            y++;
        }
    }

    [TestMethod]
    public void TestImageColumnIndexer()
    {
        IImage image = _captureZone!.Image;

        Assert.AreEqual(image.Width, image.Columns.Count);

        for (int x = 0; x < image.Width; x++)
        {
            IImage.IImageColumn column = image.Columns[x];
            Assert.AreEqual(image.Height, column.Length);
            for (int y = 0; y < column.Length; y++)
                Assert.AreEqual(TestScreenCapture.GetColorFromLocation(x, y), column[y]);
        }
    }

    [TestMethod]
    public void TestImageColumnEnumerator()
    {
        IImage image = _captureZone!.Image;

        int x = 0;
        foreach (IImage.IImageColumn column in image.Columns)
        {
            for (int y = 0; y < column.Length; y++)
                Assert.AreEqual(TestScreenCapture.GetColorFromLocation(x, y), column[y]);

            x++;
        }
    }

    #endregion
}