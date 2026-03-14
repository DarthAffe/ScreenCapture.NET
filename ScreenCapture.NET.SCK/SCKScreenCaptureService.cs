using System;

namespace ScreenCapture.NET;

public class SCKScreenCaptureService : IScreenCaptureService
{
    #region Properties & Fields


    private bool _isDisposed;

    #endregion

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="SCKScreenCaptureService"/> class.
    /// </summary>
    public SCKScreenCaptureService()
    {

    }

    private readonly Dictionary<Display, SCKScreenCapture> _screenCaptures = new();
    ~SCKScreenCaptureService() => Dispose();

    #endregion

    #region Methods

    /// <inheritdoc />
    public IEnumerable<GraphicsCard> GetGraphicsCards()
    {
        if (_isDisposed) throw new ObjectDisposedException(GetType().FullName);

        Dictionary<int, GraphicsCard> graphicsCards = new();
        // mac os doesn't need this
        return graphicsCards.Values;
    }

    /// <inheritdoc />
    public IEnumerable<Display> GetDisplays(GraphicsCard graphicsCard)

    {
        if (_isDisposed) throw new ObjectDisposedException(GetType().FullName);

        for (int i = 0; i < NSScreen.Screens.Length; i++)
        {
            yield return new Display(i, NSScreen.Screens[i].LocalizedName, (int)NSScreen.Screens[i].Frame.Width, (int)NSScreen.Screens[i].Frame.Height, Rotation.None, graphicsCard);
        }
    }

    /// <inheritdoc />
    IScreenCapture IScreenCaptureService.GetScreenCapture(Display display) => GetScreenCapture(display);
    public SCKScreenCapture GetScreenCapture(Display display)
    {
        if (_isDisposed) throw new ObjectDisposedException(GetType().FullName);

        if (!_screenCaptures.TryGetValue(display, out SCKScreenCapture? screenCapture))
            _screenCaptures.Add(display, screenCapture = new SCKScreenCapture(display, 0.25f));
        return screenCapture;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_isDisposed) return;

        foreach (SCKScreenCapture screenCapture in _screenCaptures.Values)
            screenCapture.Dispose();
        _screenCaptures.Clear();

        //dispose sck

        GC.SuppressFinalize(this);

        _isDisposed = true;
    }

    #endregion
}
