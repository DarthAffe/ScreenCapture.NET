using HPPH;

namespace ScreenCapture.NET.Tests;

[TestClass]
public sealed class ScreenCaptureServiceExtensionsTests
{
    private static readonly GraphicsCard FIRST_CARD = new("card-a", 0, "First", 1, 2);
    private static readonly GraphicsCard SECOND_CARD = new("card-b", 1, "Second", 3, 4);

    private static readonly Display FIRST_DISPLAY = CreateDisplay("display-a", 0, FIRST_CARD);
    private static readonly Display SECOND_DISPLAY = CreateDisplay("display-b", 0, SECOND_CARD);
    private static readonly Display THIRD_DISPLAY = CreateDisplay("display-c", 1, SECOND_CARD);

    private static Display CreateDisplay(string id, int index, GraphicsCard graphicsCard)
        => new(id, index, $@"\\.\DISPLAY{index + 1}", id.ToUpperInvariant(), index * 1920, 0, 1920, 1080, ImageRotation.None, graphicsCard);

    private static TestScreenCaptureService CreateService()
        => new([FIRST_CARD, SECOND_CARD], [FIRST_DISPLAY, SECOND_DISPLAY, THIRD_DISPLAY]);

    [TestMethod]
    public void GraphicsCardIsFoundById()
    {
        using TestScreenCaptureService service = CreateService();

        Assert.IsTrue(service.TryGetGraphicsCard("card-b", out GraphicsCard graphicsCard));
        Assert.AreEqual(SECOND_CARD, graphicsCard);
    }

    [TestMethod]
    public void DisplayIsFoundOnEveryGraphicsCard()
    {
        using TestScreenCaptureService service = CreateService();

        Assert.IsTrue(service.TryGetDisplay("display-a", out Display display));
        Assert.AreEqual(FIRST_DISPLAY, display);

        Assert.IsTrue(service.TryGetDisplay("display-c", out display));
        Assert.AreEqual(THIRD_DISPLAY, display);
    }

    [TestMethod]
    public void UnknownIdIsNotFound()
    {
        using TestScreenCaptureService service = CreateService();

        Assert.IsFalse(service.TryGetGraphicsCard("unknown", out GraphicsCard graphicsCard));
        Assert.AreEqual(default, graphicsCard);

        Assert.IsFalse(service.TryGetDisplay("unknown", out Display display));
        Assert.AreEqual(default, display);
    }

    [TestMethod]
    public void IdsOfTheOtherKindAreNotFound()
    {
        using TestScreenCaptureService service = CreateService();

        Assert.IsFalse(service.TryGetGraphicsCard("display-a", out _));
        Assert.IsFalse(service.TryGetDisplay("card-a", out _));
    }

    [TestMethod]
    public void IdsAreComparedOrdinally()
    {
        using TestScreenCaptureService service = CreateService();

        Assert.IsFalse(service.TryGetGraphicsCard("CARD-A", out _));
        Assert.IsFalse(service.TryGetDisplay("DISPLAY-A", out _));
        Assert.IsFalse(service.TryGetDisplay("display-a ", out _));
    }

    [TestMethod]
    public void CurrentValuesAreReturned()
    {
        using TestScreenCaptureService service = CreateService();
        Display stored = FIRST_DISPLAY;

        // The display changed since its id was stored (e.g. another resolution and index after a hot-plug).
        Display current = stored with { Index = 3, Width = 2560, Height = 1440 };
        service.Displays[0] = current;

        Assert.IsTrue(service.TryGetDisplay(stored.Id, out Display display));
        Assert.AreEqual(current, display);
    }

    [TestMethod]
    public void NullArgumentsThrow()
    {
        using TestScreenCaptureService service = CreateService();

        Assert.ThrowsExactly<ArgumentNullException>(() => service.TryGetGraphicsCard(null!, out _));
        Assert.ThrowsExactly<ArgumentNullException>(() => service.TryGetDisplay(null!, out _));
        Assert.ThrowsExactly<ArgumentNullException>(() => ((IScreenCaptureService)null!).TryGetGraphicsCard("card-a", out _));
        Assert.ThrowsExactly<ArgumentNullException>(() => ((IScreenCaptureService)null!).TryGetDisplay("display-a", out _));
    }

    [TestMethod]
    public void EnumerationErrorsArePassedOn()
    {
        using TestScreenCaptureService service = CreateService();
        service.Dispose();

        Assert.ThrowsExactly<ObjectDisposedException>(() => service.TryGetGraphicsCard("card-a", out _));
        Assert.ThrowsExactly<ObjectDisposedException>(() => service.TryGetDisplay("display-a", out _));
    }

    /// <summary>
    /// Enumerates fixed devices; like the real services, <see cref="GetDisplays"/> finds the card by its id.
    /// </summary>
    private sealed class TestScreenCaptureService(List<GraphicsCard> graphicsCards, List<Display> displays) : IScreenCaptureService
    {
        private bool _isDisposed;

        public List<Display> Displays { get; } = displays;

        public IEnumerable<GraphicsCard> GetGraphicsCards()
        {
            ObjectDisposedException.ThrowIf(_isDisposed, this);
            return graphicsCards;
        }

        public IEnumerable<Display> GetDisplays(GraphicsCard graphicsCard)
        {
            ObjectDisposedException.ThrowIf(_isDisposed, this);
            return Displays.Where(d => d.GraphicsCard.Id == graphicsCard.Id);
        }

        public IScreenCapture GetScreenCapture(Display display) => throw new NotSupportedException();

        public void Dispose() => _isDisposed = true;
    }
}
