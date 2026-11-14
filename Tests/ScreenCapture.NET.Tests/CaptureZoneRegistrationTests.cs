using HPPH;

namespace ScreenCapture.NET.Tests;

[TestClass]
public sealed class CaptureZoneRegistrationTests
{
    [TestMethod]
    public void CaptureUpdatesRegisteredZone()
    {
        using TestScreenCapture screenCapture = new();
        CaptureZone<ColorARGB> zone = screenCapture.RegisterCaptureZone(0, 0, 16, 8);

        int updates = 0;
        zone.Updated += (_, _) => updates++;

        Assert.IsTrue(screenCapture.CaptureScreen());

        Assert.AreEqual(1, updates);
        using (zone.Lock())
        {
            ReadOnlySpan<ColorARGB> pixels = zone.Pixels;
            for (int y = 0; y < zone.Height; y++)
                for (int x = 0; x < zone.Width; x++)
                    Assert.AreEqual(TestScreenCapture.GetColorFromLocation(x, y), pixels[(y * zone.Width) + x]);
        }
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
        CaptureZone<ColorARGB> zone = screenCapture.RegisterCaptureZone(0, 0, 16, 8);

        Assert.IsTrue(screenCapture.UnregisterCaptureZone(zone));
        Assert.IsFalse(screenCapture.UnregisterCaptureZone(zone));
    }

    [TestMethod]
    public void UnregisteredZoneIsNotUpdated()
    {
        using TestScreenCapture screenCapture = new();
        CaptureZone<ColorARGB> zone = screenCapture.RegisterCaptureZone(0, 0, 16, 8);

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
        CaptureZone<ColorARGB> zone = screenCapture.RegisterCaptureZone(0, 0, 16, 8);

        Assert.IsFalse(otherScreenCapture.UnregisterCaptureZone(zone));
    }

    [TestMethod]
    public void ZoneWithoutAutoUpdateIsOnlyUpdatedOnRequest()
    {
        using TestScreenCapture screenCapture = new();
        CaptureZone<ColorARGB> zone = screenCapture.RegisterCaptureZone(0, 0, 16, 8);
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

        ICaptureZone zone = capture.RegisterCaptureZone(0, 0, 16, 8);

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

        Assert.ThrowsExactly<ArgumentException>(() => capture.UnregisterCaptureZone(foreignZone));
        Assert.ThrowsExactly<ArgumentException>(() => capture.UpdateCaptureZone(foreignZone, x: 0));
    }

    [TestMethod]
    public void DisposedCaptureThrows()
    {
        TestScreenCapture screenCapture = new();
        CaptureZone<ColorARGB> zone = screenCapture.RegisterCaptureZone(0, 0, 16, 8);

        screenCapture.Dispose();
        screenCapture.Dispose(); // a second dispose is a no-op

        Assert.ThrowsExactly<ObjectDisposedException>(() => screenCapture.CaptureScreen());
        Assert.ThrowsExactly<ObjectDisposedException>(() => screenCapture.RegisterCaptureZone(0, 0, 16, 8));
        Assert.ThrowsExactly<ObjectDisposedException>(() => screenCapture.UpdateCaptureZone(zone, x: 1));
        Assert.ThrowsExactly<ObjectDisposedException>(() => screenCapture.UnregisterCaptureZone(zone));
        Assert.ThrowsExactly<ObjectDisposedException>(() => screenCapture.Restart());
    }

    private sealed class ForeignCaptureZone : ICaptureZone
    {
        public Display Display => default;
        public IColorFormat ColorFormat => ColorBGRA.ColorFormat;
        public int X => 0;
        public int Y => 0;
        public int Width => 1;
        public int Height => 1;
        public int Stride => 4;
        public int DownscaleLevel => 0;
        public int UnscaledWidth => 1;
        public int UnscaledHeight => 1;
        public ReadOnlySpan<byte> RawBuffer => [];
        public IImage Image => throw new NotSupportedException();
        public bool AutoUpdate { get; set; }
        public bool IsUpdateRequested => false;
        public event EventHandler? Updated { add { } remove { } }
        public IDisposable Lock() => throw new NotSupportedException();
        public void RequestUpdate() { }
        public RefImage<T> GetRefImage<T>() where T : unmanaged, IColor<T> => throw new NotSupportedException();
    }
}
