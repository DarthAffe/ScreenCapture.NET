using HPPH;

namespace ScreenCapture.NET.Tests;

/// <summary>
/// The pixels of a zone are only reachable through a <see cref="CaptureFrame{TColor}"/>/<see cref="CaptureFrame"/>, which holds the zone lock until it's disposed.
/// </summary>
[TestClass]
public sealed class CaptureFrameTests
{
    #region Constants

    private static readonly TimeSpan TIMEOUT = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan BLOCKED = TimeSpan.FromMilliseconds(200);

    #endregion

    #region Methods

    [TestMethod]
    public void HeldFrameBlocksCapture()
    {
        using TestScreenCapture screenCapture = new();
        CaptureZone<ColorARGB> zone = screenCapture.RegisterCaptureZone(0, 0, 16, 8);

        int updates = 0;
        screenCapture.ZoneUpdating = _ => Interlocked.Increment(ref updates);

        CaptureFrame<ColorARGB> frame = zone.Lock();
        Task<bool> capture;
        try
        {
            capture = Task.Run(screenCapture.CaptureScreen);

            Assert.IsFalse(capture.Wait(BLOCKED), "The capture finished while a frame was held.");
            Assert.AreEqual(0, Volatile.Read(ref updates), "The zone was updated while a frame was held.");
        }
        finally
        {
            frame.Dispose();
        }

        Assert.IsTrue(capture.Wait(TIMEOUT), "The capture didn't continue after the frame was disposed.");
        Assert.IsTrue(capture.Result);
        Assert.AreEqual(1, updates);
    }

    [TestMethod]
    public void NonGenericFrameHoldsTheLock()
    {
        using TestScreenCapture screenCapture = new();
        ICaptureZone zone = screenCapture.RegisterCaptureZone(0, 0, 16, 8);

        CaptureFrame frame = zone.Lock();
        try
        {
            Assert.IsFalse(CanLockFromOtherThread(zone, BLOCKED), "Another thread could lock the zone while a frame was held.");
        }
        finally
        {
            frame.Dispose();
        }

        Assert.IsTrue(CanLockFromOtherThread(zone, TIMEOUT), "The lock wasn't released.");
    }

    [TestMethod]
    public void DisposingTwiceReleasesOnlyOnce()
    {
        using TestScreenCapture screenCapture = new();
        CaptureZone<ColorARGB> zone = screenCapture.RegisterCaptureZone(0, 0, 16, 8);

        CaptureFrame<ColorARGB> outer = zone.Lock();
        try
        {
            CaptureFrame<ColorARGB> inner = zone.Lock();
            inner.Dispose();
            inner.Dispose();

            Assert.IsFalse(CanLockFromOtherThread(zone, BLOCKED), "Disposing the inner frame twice released the lock of the outer one.");
        }
        finally
        {
            outer.Dispose();
        }

        Assert.IsTrue(CanLockFromOtherThread(zone, TIMEOUT), "The lock wasn't released.");
    }

    [TestMethod]
    public void FrameShowsCapturedPixels()
    {
        using TestScreenCapture screenCapture = new();
        CaptureZone<ColorARGB> zone = screenCapture.RegisterCaptureZone(0, 0, 16, 8);
        screenCapture.CaptureScreen();

        using CaptureFrame<ColorARGB> frame = zone.Lock();

        Assert.AreEqual(ColorARGB.ColorFormat, frame.ColorFormat);
        Assert.AreEqual((16, 8, 16 * 4), (frame.Width, frame.Height, frame.Stride));
        Assert.AreEqual(frame.Stride * frame.Height, frame.RawBuffer.Length);
        Assert.AreEqual(frame.Width * frame.Height, frame.Pixels.Length);

        RefImage<ColorARGB> image = frame.Image;
        Assert.AreEqual((frame.Width, frame.Height), (image.Width, image.Height));
        for (int y = 0; y < frame.Height; y++)
            for (int x = 0; x < frame.Width; x++)
            {
                Assert.AreEqual(TestScreenCapture.GetColorFromLocation(x, y), image[x, y]);
                Assert.AreEqual(image[x, y], frame.Pixels[(y * frame.Width) + x]);
            }
    }

    [TestMethod]
    public void CopyImageIsACopy()
    {
        using TestScreenCapture screenCapture = new();
        CaptureZone<ColorARGB> zone = screenCapture.RegisterCaptureZone(0, 0, 16, 8);
        screenCapture.CaptureScreen();

        Image<ColorARGB> image;
        IImage untypedImage;
        using (CaptureFrame<ColorARGB> frame = zone.Lock())
            image = frame.CopyImage();
        using (CaptureFrame frame = ((ICaptureZone)zone).Lock())
            untypedImage = frame.CopyImage();

        using (zone.EnterLockScope())
            zone.InternalBuffer.AsSpan().Clear();

        Assert.AreEqual(ColorARGB.ColorFormat, untypedImage.ColorFormat);
        Assert.AreSame(image.GetType(), untypedImage.GetType());
        Assert.IsTrue(image.ContentEquals((Image<ColorARGB>)untypedImage));
        for (int y = 0; y < image.Height; y++)
            for (int x = 0; x < image.Width; x++)
                Assert.AreEqual(TestScreenCapture.GetColorFromLocation(x, y), image[x, y]);
    }

    /// <summary>
    /// The lock is reentrant, so the thread holding a frame can resize the zone. The frame keeps the size and buffer it was created with.
    /// </summary>
    [TestMethod]
    public void FrameKeepsTheZoneAsItWasWhenLocked()
    {
        using TestScreenCapture screenCapture = new();
        CaptureZone<ColorARGB> zone = screenCapture.RegisterCaptureZone(0, 0, 16, 8);
        screenCapture.CaptureScreen();

        using (CaptureFrame<ColorARGB> frame = zone.Lock())
        {
            screenCapture.UpdateCaptureZone(zone, width: 32, height: 4);

            Assert.AreEqual((32, 4), (zone.Width, zone.Height));
            Assert.AreEqual((16, 8, 16 * 4), (frame.Width, frame.Height, frame.Stride));
            Assert.AreEqual(16 * 4 * 8, frame.RawBuffer.Length);
            Assert.AreEqual(TestScreenCapture.GetColorFromLocation(15, 7), frame.Image[15, 7]);
        }

        using (CaptureFrame<ColorARGB> frame = zone.Lock())
            Assert.AreEqual((32, 4, 32 * 4 * 4), (frame.Width, frame.Height, frame.RawBuffer.Length));
    }

    [TestMethod]
    public void NonGenericFrameGivesTypedAccess()
    {
        using TestScreenCapture screenCapture = new();
        ICaptureZone zone = screenCapture.RegisterCaptureZone(0, 0, 16, 8);
        screenCapture.CaptureScreen();

        using CaptureFrame frame = zone.Lock();

        Assert.AreEqual(ColorARGB.ColorFormat, frame.ColorFormat);
        Assert.AreEqual((16, 8, 16 * 4), (frame.Width, frame.Height, frame.Stride));

        RefImage<ColorARGB> image = frame.AsRefImage<ColorARGB>();
        Assert.AreEqual(TestScreenCapture.GetColorFromLocation(15, 7), image[15, 7]);

        try
        {
            frame.AsRefImage<ColorBGRA>();
            Assert.Fail("A wrong color format was accepted.");
        }
        catch (ArgumentException) { }

        (Type colorType, int width, int height, IColor lastPixel) expected = (typeof(ColorARGB), 16, 8, TestScreenCapture.GetColorFromLocation(15, 7));
        Assert.AreEqual(expected, frame.Execute<DescribeOperation, (Type, int, int, IColor)>(default));
        Assert.AreEqual(expected, frame.Execute(new DescribeOperation()));
    }

    private static bool CanLockFromOtherThread(ICaptureZone zone, TimeSpan timeout)
        => Task.Run(() => { using (zone.Lock()) { } }).Wait(timeout);

    #endregion

    private readonly struct DescribeOperation : IImageOperation<(Type colorType, int width, int height, IColor lastPixel)>
    {
        public (Type colorType, int width, int height, IColor lastPixel) Execute<T>(RefImage<T> image)
            where T : unmanaged, IColor<T>
            => (typeof(T), image.Width, image.Height, image[image.Width - 1, image.Height - 1]);
    }
}
