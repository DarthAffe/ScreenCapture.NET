using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace ScreenCapture.NET;

/// <summary>
/// Represents a <see cref="IScreenCaptureService"/> using the <see cref="X11ScreenCapture"/>.
/// </summary>
/// <remarks>
/// The first connection to the X server installs a process-wide Xlib error handler (<c>XSetErrorHandler</c>) that stays installed.
/// It records errors of this library's connections (a failed capture restarts instead of Xlib terminating the process)
/// and passes errors of all other connections to the handler that was installed before.
/// If other code installs its own handler afterwards without passing errors on, X errors of the capture are handled by that code.
/// </remarks>
public sealed class X11ScreenCaptureService : IScreenCaptureService
{
    #region Properties & Fields

    private readonly Dictionary<string, X11ScreenCapture> _screenCaptures = new(StringComparer.Ordinal);

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
    /// <exception cref="InvalidOperationException">No connection to an X server could be opened.</exception>
    public IEnumerable<GraphicsCard> GetGraphicsCards()
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        using X11DisplayHandle display = OpenDisplay();

        string name = GetDisplayName(display);
        return [new GraphicsCard(name, 0, name, 0, 0)];
    }

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">No connection to an X server could be opened.</exception>
    public IEnumerable<Display> GetDisplays(GraphicsCard graphicsCard)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        using X11DisplayHandle display = OpenDisplay();

        string name = GetDisplayName(display);
        if (!string.Equals(graphicsCard.Id, name, StringComparison.Ordinal)) return [];

        graphicsCard = graphicsCard with { Index = 0 };

        int screenCount = X11.XScreenCount(display);
        List<Display> displays = new(screenCount);
        for (int screenNumber = 0; screenNumber < screenCount; screenNumber++)
        {
            nint screen = X11.XScreenOfDisplay(display, screenNumber);
            int screenWidth = X11.XWidthOfScreen(screen);
            int screenHeight = X11.XHeightOfScreen(screen);

            // DarthAffe 10.09.2023: Emulate DX-Displaynames for no real reason ¯\(°_o)/¯
            displays.Add(new Display($"{name}/{screenNumber}", screenNumber, @$"\\.\DISPLAY{screenNumber + 1}", string.Empty, 0, 0, screenWidth, screenHeight, Rotation.None, graphicsCard));
        }

        return displays;
    }

    private static string GetDisplayName(X11DisplayHandle display) => Marshal.PtrToStringAnsi(X11.XDisplayString(display)) ?? string.Empty;

    // The enumeration is eager, so the connection is closed before the caller sees the results and a missing X server throws right away.
    private static X11DisplayHandle OpenDisplay()
    {
        X11DisplayHandle display = X11DisplayHandle.Open();
        if (!display.IsInvalid) return display;

        display.Dispose();

        throw X11DisplayHandle.CreateOpenFailedException();
    }

    /// <inheritdoc />
    IScreenCapture IScreenCaptureService.GetScreenCapture(Display display) => GetScreenCapture(display);

    /// <inheritdoc cref="IScreenCaptureService.GetScreenCapture"/>
    public X11ScreenCapture GetScreenCapture(Display display)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        if (!_screenCaptures.TryGetValue(display.Id, out X11ScreenCapture? screenCapture))
            _screenCaptures.Add(display.Id, screenCapture = new X11ScreenCapture(display));
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