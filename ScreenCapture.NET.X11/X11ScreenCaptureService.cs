using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace ScreenCapture.NET;

/// <summary>
/// Represents a <see cref="IScreenCaptureService"/> using the <see cref="X11ScreenCapture"/>.
/// </summary>
public sealed class X11ScreenCaptureService : IScreenCaptureService
{
    #region Properties & Fields

    private readonly Dictionary<Display, X11ScreenCapture> _screenCaptures = new();

    private bool _isDisposed;

    #endregion

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="X11ScreenCaptureService"/> class.
    /// </summary>
    public X11ScreenCaptureService()
    { }

    #endregion

    #region Methods

    /// <inheritdoc />
    public IEnumerable<GraphicsCard> GetGraphicsCards()
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        nint display = X11.XOpenDisplay(X11.DISPLAY_NAME);
        try
        {
            string name = Marshal.PtrToStringAnsi(X11.XDisplayString(display)) ?? string.Empty;
            yield return new GraphicsCard(0, name, 0, 0);
        }
        finally
        {
            X11.XCloseDisplay(display);
        }
    }

    /// <inheritdoc />
    public IEnumerable<Display> GetDisplays(GraphicsCard graphicsCard)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        nint display = X11.XOpenDisplay(X11.DISPLAY_NAME);
        try
        {
            int screenCount = X11.XScreenCount(display);
            for (int screenNumber = 0; screenNumber < screenCount; screenNumber++)
            {
                nint screen = X11.XScreenOfDisplay(display, screenNumber);
                int screenWidth = X11.XWidthOfScreen(screen);
                int screenHeight = X11.XHeightOfScreen(screen);

                // DarthAffe 10.09.2023: Emulate DX-Displaynames for no real reason ¯\(°_o)/¯
                yield return new Display(screenNumber, @$"\\.\DISPLAY{screenNumber + 1}", screenWidth, screenHeight, Rotation.None, graphicsCard);
            }
        }
        finally
        {
            X11.XCloseDisplay(display);
        }
    }

    /// <inheritdoc />
    IScreenCapture IScreenCaptureService.GetScreenCapture(Display display) => GetScreenCapture(display);

    /// <inheritdoc cref="IScreenCaptureService.GetScreenCapture"/>
    public X11ScreenCapture GetScreenCapture(Display display)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        if (!_screenCaptures.TryGetValue(display, out X11ScreenCapture? screenCapture))
            _screenCaptures.Add(display, screenCapture = new X11ScreenCapture(display));
        return screenCapture;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_isDisposed) return;

        foreach (X11ScreenCapture screenCapture in _screenCaptures.Values)
            screenCapture.Dispose();
        _screenCaptures.Clear();

        _isDisposed = true;
    }

    #endregion
}