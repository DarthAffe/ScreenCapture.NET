using System;
using System.Collections.Generic;
using System.Windows.Forms;

namespace ScreenCapture.NET;

/// <summary>
/// Represents a <see cref="IScreenCaptureService"/> using the <see cref="GDIScreenCapture"/>.
/// </summary>
public class GDIScreenCaptureService : IScreenCaptureService
{
    #region Properties & Fields

    private readonly Dictionary<Display, GDIScreenCapture> _screenCaptures = new();

    private bool _isDisposed;

    #endregion

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="GDIScreenCaptureService"/> class.
    /// </summary>
    public GDIScreenCaptureService()
    {
    }

    ~GDIScreenCaptureService() => Dispose();

    #endregion

    #region Methods

    /// <inheritdoc />
    public IEnumerable<GraphicsCard> GetGraphicsCards()
    {
        if (_isDisposed) throw new ObjectDisposedException(GetType().FullName);

        return [new GraphicsCard(0, "Default", 0, 0)];
    }

    /// <inheritdoc />
    public IEnumerable<Display> GetDisplays(GraphicsCard graphicsCard)
    {
        if (_isDisposed) throw new ObjectDisposedException(GetType().FullName);

        Screen screen = Screen.PrimaryScreen!;
        return [new Display(0, screen.DeviceName, screen.Bounds.Width, screen.Bounds.Height, Rotation.None, graphicsCard)];
    }

    /// <inheritdoc />
    IScreenCapture IScreenCaptureService.GetScreenCapture(Display display) => GetScreenCapture(display);
    public GDIScreenCapture GetScreenCapture(Display display)
    {
        if (_isDisposed) throw new ObjectDisposedException(GetType().FullName);

        if (!_screenCaptures.TryGetValue(display, out GDIScreenCapture? screenCapture))
            _screenCaptures.Add(display, screenCapture = new GDIScreenCapture(display));
        return screenCapture;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_isDisposed) return;

        foreach (GDIScreenCapture screenCapture in _screenCaptures.Values)
            screenCapture.Dispose();
        _screenCaptures.Clear();

        GC.SuppressFinalize(this);

        _isDisposed = true;
    }

    #endregion
}