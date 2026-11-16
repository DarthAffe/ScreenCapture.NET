using HPPH;

namespace ScreenCapture.NET.Tests;

[TestClass]
public sealed class CaptureZoneRegistrationTests
{
    [TestMethod]
    public void CaptureUpdatesRegisteredZone()
    {
        using TestScreenCapture screenCapture = new();
        CaptureZone<ColorARGB> zone = screenCapture.RegisterCaptureZone(new CaptureRegion(0, 0, 16, 8));

        int updates = 0;
        zone.Updated += (_, _) => updates++;

        Assert.IsTrue(screenCapture.CaptureScreen());

        Assert.AreEqual(1, updates);
        using CaptureFrame<ColorARGB> frame = zone.Lock();
        ReadOnlySpan<ColorARGB> pixels = frame.Pixels;
        for (int y = 0; y < frame.Height; y++)
            for (int x = 0; x < frame.Width; x++)
                Assert.AreEqual(TestScreenCapture.GetColorFromLocation(x, y), pixels[(y * frame.Width) + x]);
    }

    [TestMethod]
    public void CaptureRaisesUpdatedEvent()
    {
        using TestScreenCapture screenCapture = new();

        ScreenCaptureUpdatedEventArgs? args = null;
        screenCapture.Updated += (_, e) => args = e;

        screenCapture.CaptureScreen();

        Assert.IsNotNull(args);
        Assert.IsTrue(args.IsSuccessful);
    }

    [TestMethod]
    public void UnregisterReturnsWhetherZoneWasRegistered()
    {
        using TestScreenCapture screenCapture = new();
        CaptureZone<ColorARGB> zone = screenCapture.RegisterCaptureZone(new CaptureRegion(0, 0, 16, 8));

        Assert.IsTrue(screenCapture.UnregisterCaptureZone(zone));
        Assert.IsFalse(screenCapture.UnregisterCaptureZone(zone));
    }

    [TestMethod]
    public void UnregisteredZoneIsNotUpdated()
    {
        using TestScreenCapture screenCapture = new();
        CaptureZone<ColorARGB> zone = screenCapture.RegisterCaptureZone(new CaptureRegion(0, 0, 16, 8));

        int updates = 0;
        zone.Updated += (_, _) => updates++;

        screenCapture.UnregisterCaptureZone(zone);
        screenCapture.CaptureScreen();

        Assert.AreEqual(0, updates);
    }

    [TestMethod]
    public void UnregisteringZoneOfOtherCaptureReturnsFalse()
    {
        using TestScreenCapture screenCapture = new();
        using TestScreenCapture otherScreenCapture = new();
        CaptureZone<ColorARGB> zone = screenCapture.RegisterCaptureZone(new CaptureRegion(0, 0, 16, 8));

        Assert.IsFalse(otherScreenCapture.UnregisterCaptureZone(zone));
    }

    [TestMethod]
    public void ZoneWithoutAutoUpdateIsOnlyUpdatedOnRequest()
    {
        using TestScreenCapture screenCapture = new();
        CaptureZone<ColorARGB> zone = screenCapture.RegisterCaptureZone(new CaptureRegion(0, 0, 16, 8));
        zone.AutoUpdate = false;

        int updates = 0;
        zone.Updated += (_, _) => updates++;

        screenCapture.CaptureScreen();
        Assert.AreEqual(0, updates);

        zone.RequestUpdate();
        Assert.IsTrue(zone.IsUpdateRequested);

        screenCapture.CaptureScreen();
        Assert.AreEqual(1, updates);
        Assert.IsFalse(zone.IsUpdateRequested);

        screenCapture.CaptureScreen();
        Assert.AreEqual(1, updates);
    }

    [TestMethod]
    public void InterfaceRegistrationReturnsTypedZone()
    {
        using TestScreenCapture screenCapture = new();
        IScreenCapture capture = screenCapture;

        ICaptureZone zone = capture.RegisterCaptureZone(new CaptureRegion(0, 0, 16, 8));

        Assert.IsInstanceOfType<CaptureZone<ColorARGB>>(zone);
        Assert.AreEqual(ColorARGB.ColorFormat, zone.ColorFormat);
        Assert.IsTrue(capture.UnregisterCaptureZone(zone));
    }

    [TestMethod]
    public void InterfaceRejectsZoneOfOtherColorFormat()
    {
        using TestScreenCapture screenCapture = new();
        IScreenCapture capture = screenCapture;
        ICaptureZone foreignZone = new ForeignCaptureZone();

        Assert.AreEqual("captureZone", Assert.ThrowsExactly<ArgumentException>(() => capture.UnregisterCaptureZone(foreignZone)).ParamName);
        Assert.AreEqual("captureZone", Assert.ThrowsExactly<ArgumentException>(() => capture.UpdateCaptureZone(foreignZone, new CaptureRegion(0, 0, 1, 1))).ParamName);
    }

    [TestMethod]
    public void NullZoneThrows()
    {
        using TestScreenCapture screenCapture = new();
        IScreenCapture capture = screenCapture;

        Assert.ThrowsExactly<ArgumentNullException>(() => screenCapture.UnregisterCaptureZone(null!));
        Assert.ThrowsExactly<ArgumentNullException>(() => capture.UnregisterCaptureZone(null!));
        Assert.ThrowsExactly<ArgumentNullException>(() => capture.UpdateCaptureZone(null!, new CaptureRegion(0, 0, 1, 1)));
    }

    [TestMethod]
    public void DisposedCaptureThrows()
    {
        TestScreenCapture screenCapture = new();
        CaptureZone<ColorARGB> zone = screenCapture.RegisterCaptureZone(new CaptureRegion(0, 0, 16, 8));

        screenCapture.Dispose();
        screenCapture.Dispose(); // a second dispose is a no-op

        Assert.ThrowsExactly<ObjectDisposedException>(() => screenCapture.CaptureScreen());
        Assert.ThrowsExactly<ObjectDisposedException>(() => screenCapture.RegisterCaptureZone(new CaptureRegion(0, 0, 16, 8)));
        Assert.ThrowsExactly<ObjectDisposedException>(() => screenCapture.UpdateCaptureZone(zone, zone.Region with { X = 1 }));
        Assert.ThrowsExactly<ObjectDisposedException>(() => screenCapture.UnregisterCaptureZone(zone));
        Assert.ThrowsExactly<ObjectDisposedException>(() => screenCapture.Restart());
    }

    private sealed class ForeignCaptureZone : ICaptureZone
    {
        public Display Display => default;
        public IColorFormat ColorFormat => ColorBGRA.ColorFormat;
        public CaptureRegion Region => new(0, 0, 1, 1);
        public int Width => 1;
        public int Height => 1;
        public int Stride => 4;
        public int DownscaleLevel => 0;
        public bool IsDoubleBuffered => true;
        public bool AutoUpdate { get; set; }
        public bool IsUpdateRequested => false;
        public event EventHandler? Updated { add { } remove { } }
        public CaptureFrame Lock() => throw new NotSupportedException();
        public void RequestUpdate() { }
    }
}
