using HPPH;

namespace ScreenCapture.NET.Tests;

/// <summary>
/// The pixels of a zone are only reachable through a <see cref="CaptureFrame{TColor}"/>/<see cref="CaptureFrame"/>, and they don't change while the frame is held.
/// Double-buffered zones are written into a second buffer meanwhile, single-buffered zones make the capture wait.
/// </summary>
[TestClass]
public sealed class CaptureFrameTests
{
    #region Constants

    private static readonly TimeSpan TIMEOUT = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan BLOCKED = TimeSpan.FromMilliseconds(200);

    private static readonly ColorARGB COLOR_A = new(255, 1, 2, 3);
    private static readonly ColorARGB COLOR_B = new(255, 4, 5, 6);
    private static readonly ColorARGB COLOR_C = new(255, 7, 8, 9);

    #endregion

    #region Methods

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void ZonesAreDoubleBufferedByDefault(bool doubleBuffered)
    {
        using TestScreenCapture screenCapture = new();

        Assert.IsTrue(screenCapture.RegisterCaptureZone(new CaptureRegion(0, 0, 16, 8)).IsDoubleBuffered);
        Assert.AreEqual(doubleBuffered, screenCapture.RegisterCaptureZone(new CaptureRegion(0, 0, 16, 8), doubleBuffered: doubleBuffered).IsDoubleBuffered);
        Assert.AreEqual(doubleBuffered, ((IScreenCapture)screenCapture).RegisterCaptureZone(new CaptureRegion(0, 0, 16, 8), doubleBuffered: doubleBuffered).IsDoubleBuffered);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void HeldFrameDoesNotBlockCaptureOfDoubleBufferedZone(bool nonGeneric)
    {
        using TestScreenCapture screenCapture = new() { Pattern = (_, _) => COLOR_A };
        CaptureZone<ColorARGB> zone = screenCapture.RegisterCaptureZone(new CaptureRegion(0, 0, 16, 8));
        screenCapture.CaptureScreen();

        int updates = 0;
        zone.Updated += (_, _) => Interlocked.Increment(ref updates);
        screenCapture.Pattern = (_, _) => COLOR_B;

        if (nonGeneric)
        {
            using CaptureFrame frame = ((ICaptureZone)zone).Lock();

            Assert.IsTrue(Task.Run(screenCapture.CaptureScreen).Wait(TIMEOUT), "The capture waited for the frame.");
            AssertAll(COLOR_A, frame.AsRefImage<ColorARGB>(), "The pixels of a held frame changed.");
        }
        else
        {
            using CaptureFrame<ColorARGB> frame = zone.Lock();

            Assert.IsTrue(Task.Run(screenCapture.CaptureScreen).Wait(TIMEOUT), "The capture waited for the frame.");
            AssertAll(COLOR_A, frame.Image, "The pixels of a held frame changed.");
        }

        Assert.AreEqual(1, updates, "The zone wasn't updated while a frame was held.");
        using (CaptureFrame<ColorARGB> frame = zone.Lock())
            AssertAll(COLOR_B, frame.Image, "A new frame doesn't show the new pixels.");
    }

    /// <summary>
    /// A frame held across two captures blocks the back buffer of the second one: the zone skips it, and the next capture after the frame is disposed fills it.
    /// </summary>
    [TestMethod]
    public void FrameHeldAcrossTwoCapturesMakesTheZoneSkipOne()
    {
        using TestScreenCapture screenCapture = new() { Pattern = (_, _) => COLOR_A };
        CaptureZone<ColorARGB> zone = screenCapture.RegisterCaptureZone(new CaptureRegion(0, 0, 16, 8));
        zone.AutoUpdate = false;
        zone.RequestUpdate();
        screenCapture.CaptureScreen();

        int updates = 0;
        zone.Updated += (_, _) => updates++;

        CaptureFrame<ColorARGB> held = zone.Lock();
        try
        {
            screenCapture.Pattern = (_, _) => COLOR_B;
            zone.RequestUpdate();
            screenCapture.CaptureScreen();
            Assert.AreEqual(1, updates, "The first capture didn't update the zone.");

            screenCapture.Pattern = (_, _) => COLOR_C;
            zone.RequestUpdate();
            screenCapture.CaptureScreen();
            Assert.AreEqual(1, updates, "The second capture updated the zone although its back buffer is held.");
            Assert.IsTrue(zone.IsUpdateRequested, "The skipped update is no longer requested.");

            AssertAll(COLOR_A, held.Image, "The pixels of a held frame changed.");
        }
        finally
        {
            held.Dispose();
        }

        using (CaptureFrame<ColorARGB> frame = zone.Lock())
            AssertAll(COLOR_B, frame.Image, "The zone doesn't show the last written pixels.");

        screenCapture.CaptureScreen();

        Assert.AreEqual(2, updates, "The zone wasn't updated after the frame was disposed.");
        Assert.IsFalse(zone.IsUpdateRequested);
        using (CaptureFrame<ColorARGB> frame = zone.Lock())
            AssertAll(COLOR_C, frame.Image, "The zone doesn't show the new pixels.");
    }

    [TestMethod]
    public void DisposingTwiceReleasesOnlyOnceInDoubleBufferedZone()
    {
        using TestScreenCapture screenCapture = new();
        CaptureZone<ColorARGB> zone = screenCapture.RegisterCaptureZone(new CaptureRegion(0, 0, 16, 8));

        int updates = 0;
        zone.Updated += (_, _) => updates++;

        CaptureFrame<ColorARGB> outer = zone.Lock();
        try
        {
            CaptureFrame<ColorARGB> inner = zone.Lock();
            inner.Dispose();
            inner.Dispose();

            screenCapture.CaptureScreen();
            screenCapture.CaptureScreen();
            Assert.AreEqual(1, updates, "Disposing the inner frame twice released the buffer of the outer one.");
        }
        finally
        {
            outer.Dispose();
        }

        screenCapture.CaptureScreen();
        Assert.AreEqual(2, updates, "The buffer wasn't released.");
    }

    [TestMethod]
    public void HeldFrameBlocksCaptureOfSingleBufferedZone()
    {
        using TestScreenCapture screenCapture = new();
        CaptureZone<ColorARGB> zone = screenCapture.RegisterCaptureZone(new CaptureRegion(0, 0, 16, 8), doubleBuffered: false);

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
    public void NonGenericFrameHoldsTheLockOfSingleBufferedZone()
    {
        using TestScreenCapture screenCapture = new();
        ICaptureZone zone = screenCapture.RegisterCaptureZone(new CaptureRegion(0, 0, 16, 8), doubleBuffered: false);

        CaptureFrame frame = zone.Lock();
        try
        {
            Assert.IsFalse(CanCaptureFromOtherThread(screenCapture, BLOCKED), "The capture finished while a frame was held.");
        }
        finally
        {
            frame.Dispose();
        }

        Assert.IsTrue(CanCaptureFromOtherThread(screenCapture, TIMEOUT), "The lock wasn't released.");
    }

    [TestMethod]
    public void DisposingTwiceReleasesOnlyOnceInSingleBufferedZone()
    {
        using TestScreenCapture screenCapture = new();
        CaptureZone<ColorARGB> zone = screenCapture.RegisterCaptureZone(new CaptureRegion(0, 0, 16, 8), doubleBuffered: false);

        CaptureFrame<ColorARGB> outer = zone.Lock();
        try
        {
            CaptureFrame<ColorARGB> inner = zone.Lock();
            inner.Dispose();
            inner.Dispose();

            Assert.IsFalse(CanCaptureFromOtherThread(screenCapture, BLOCKED), "Disposing the inner frame twice released the lock of the outer one.");
        }
        finally
        {
            outer.Dispose();
        }

        Assert.IsTrue(CanCaptureFromOtherThread(screenCapture, TIMEOUT), "The lock wasn't released.");
    }

    /// <summary>
    /// For a double-buffered zone, the back buffer would contain an older frame (or zeros), so it must not be shown.
    /// </summary>
    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void UpdateWithoutPixelsKeepsTheZone(bool doubleBuffered)
    {
        using TestScreenCapture screenCapture = new() { Pattern = (_, _) => COLOR_A };
        CaptureZone<ColorARGB> zone = screenCapture.RegisterCaptureZone(new CaptureRegion(0, 0, 16, 8), doubleBuffered: doubleBuffered);
        screenCapture.CaptureScreen();

        int updates = 0;
        zone.Updated += (_, _) => updates++;
        screenCapture.WriteZones = false;

        for (int i = 0; i < 3; i++)
            screenCapture.CaptureScreen();

        Assert.AreEqual(0, updates, "The zone was reported as updated without new pixels.");
        using CaptureFrame<ColorARGB> frame = zone.Lock();
        AssertAll(COLOR_A, frame.Image, "The zone lost its pixels.");
    }

    [TestMethod]
    public void FrameShowsCapturedPixels()
    {
        using TestScreenCapture screenCapture = new();
        CaptureZone<ColorARGB> zone = screenCapture.RegisterCaptureZone(new CaptureRegion(0, 0, 16, 8));
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
    [DataRow(true)]
    [DataRow(false)]
    public void CopyImageIsACopy(bool doubleBuffered)
    {
        using TestScreenCapture screenCapture = new();
        CaptureZone<ColorARGB> zone = screenCapture.RegisterCaptureZone(new CaptureRegion(0, 0, 16, 8), doubleBuffered: doubleBuffered);
        screenCapture.CaptureScreen();

        Image<ColorARGB> image;
        IImage untypedImage;
        using (CaptureFrame<ColorARGB> frame = zone.Lock())
            image = frame.CopyImage();
        using (CaptureFrame frame = ((ICaptureZone)zone).Lock())
            untypedImage = frame.CopyImage();

        // Overwrites every buffer of the zone.
        screenCapture.Pattern = (_, _) => COLOR_A;
        screenCapture.CaptureScreen();
        screenCapture.CaptureScreen();
        using (CaptureFrame<ColorARGB> frame = zone.Lock())
            AssertAll(COLOR_A, frame.Image, "The zone wasn't overwritten.");

        Assert.AreEqual(ColorARGB.ColorFormat, untypedImage.ColorFormat);
        Assert.AreSame(image.GetType(), untypedImage.GetType());
        Assert.IsTrue(image.ContentEquals((Image<ColorARGB>)untypedImage));
        for (int y = 0; y < image.Height; y++)
            for (int x = 0; x < image.Width; x++)
                Assert.AreEqual(TestScreenCapture.GetColorFromLocation(x, y), image[x, y]);
    }

    /// <summary>
    /// The thread holding a frame can resize the zone. The frame keeps the size and pixels it was created with,
    /// and new frames show the last captured pixels (in the old size) until the next capture.
    /// </summary>
    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void FrameKeepsItsSizeWhenTheZoneIsResized(bool doubleBuffered)
    {
        using TestScreenCapture screenCapture = new();
        CaptureZone<ColorARGB> zone = screenCapture.RegisterCaptureZone(new CaptureRegion(0, 0, 16, 8), doubleBuffered: doubleBuffered);
        screenCapture.CaptureScreen();

        using (CaptureFrame<ColorARGB> frame = zone.Lock())
        {
            screenCapture.UpdateCaptureZone(zone, zone.Region with { Width = 32, Height = 4 });

            Assert.AreEqual((32, 4), (zone.Width, zone.Height));
            Assert.AreEqual((16, 8, 16 * 4), (frame.Width, frame.Height, frame.Stride));
            Assert.AreEqual(16 * 4 * 8, frame.RawBuffer.Length);
            Assert.AreEqual(TestScreenCapture.GetColorFromLocation(15, 7), frame.Image[15, 7]);
        }

        using (CaptureFrame<ColorARGB> frame = zone.Lock())
        {
            Assert.AreEqual((16, 8), (frame.Width, frame.Height), "The frame before the next capture doesn't show the last captured pixels.");
            Assert.AreEqual(TestScreenCapture.GetColorFromLocation(15, 7), frame.Image[15, 7]);
        }

        screenCapture.CaptureScreen();

        using (CaptureFrame<ColorARGB> frame = zone.Lock())
        {
            Assert.AreEqual((32, 4, 32 * 4 * 4), (frame.Width, frame.Height, frame.RawBuffer.Length));
            Assert.AreEqual(TestScreenCapture.GetColorFromLocation(31, 3), frame.Image[31, 3]);
        }
    }

    [TestMethod]
    public void NonGenericFrameGivesTypedAccess()
    {
        using TestScreenCapture screenCapture = new();
        ICaptureZone zone = screenCapture.RegisterCaptureZone(new CaptureRegion(0, 0, 16, 8));
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

    private static bool CanCaptureFromOtherThread(TestScreenCapture screenCapture, TimeSpan timeout)
        => Task.Run(screenCapture.CaptureScreen).Wait(timeout);

    private static void AssertAll(ColorARGB expected, RefImage<ColorARGB> image, string message)
    {
        foreach (ColorARGB color in image)
            Assert.AreEqual(expected, color, message);
    }

    #endregion

    private readonly struct DescribeOperation : IImageOperation<(Type colorType, int width, int height, IColor lastPixel)>
    {
        public (Type colorType, int width, int height, IColor lastPixel) Execute<T>(RefImage<T> image)
            where T : unmanaged, IColor<T>
            => (typeof(T), image.Width, image.Height, image[image.Width - 1, image.Height - 1]);
    }
}
