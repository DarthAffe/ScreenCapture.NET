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
/// <para>Captures get the <see cref="Options"/> of the service, or the ones passed to <see cref="GetScreenCapture(Display, X11CaptureOptions)"/>.</para>
/// </remarks>
public sealed class X11ScreenCaptureService : IScreenCaptureService
{
    #region Properties & Fields

    private readonly ScreenCaptureCache<X11ScreenCapture, X11CaptureOptions> _screenCaptures;

    /// <summary>
    /// Gets the options of captures created without their own options.
    /// </summary>
    public X11CaptureOptions Options { get; }

    /// <summary>
    /// Gets the name of the X display (server) the service and its captures connect to (e.g. <c>:0</c>); <c>null</c> for the one named by $DISPLAY.
    /// </summary>
    public string? DisplayName { get; }

    #endregion

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="X11ScreenCaptureService"/> class.
    /// </summary>
    /// <remarks>
    /// The connection is opened when it's needed, so a display name that can't be opened only throws on enumeration (and makes captures fail).
    /// </remarks>
    /// <param name="options">The options of captures created without their own options; <c>null</c> for <see cref="X11CaptureOptions.Default"/>.</param>
    /// <param name="displayName">The name of the X display (server) to connect to (e.g. <c>:0</c> or <c>remote:10.0</c>); <c>null</c> for the one named by $DISPLAY.</param>
    public X11ScreenCaptureService(X11CaptureOptions? options = null, string? displayName = null)
    {
        Options = options ?? X11CaptureOptions.Default;
        DisplayName = displayName;
        _screenCaptures = new ScreenCaptureCache<X11ScreenCapture, X11CaptureOptions>(this, (display, captureOptions) => new X11ScreenCapture(display, captureOptions, DisplayName), capture => capture.Options);
    }

    #endregion

    #region Methods

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">No connection to an X server could be opened.</exception>
    public IEnumerable<GraphicsCard> GetGraphicsCards()
    {
        ObjectDisposedException.ThrowIf(_screenCaptures.IsDisposed, this);

        using X11DisplayHandle display = OpenDisplay();

        string name = GetDisplayName(display);
        return [new GraphicsCard(name, 0, name, 0, 0)];
    }

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">No connection to an X server could be opened.</exception>
    public IEnumerable<Display> GetDisplays(GraphicsCard graphicsCard)
    {
        ObjectDisposedException.ThrowIf(_screenCaptures.IsDisposed, this);

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
    private X11DisplayHandle OpenDisplay()
    {
        X11DisplayHandle display = X11DisplayHandle.Open(DisplayName);
        if (!display.IsInvalid) return display;

        display.Dispose();

        throw X11DisplayHandle.CreateOpenFailedException(DisplayName);
    }

    /// <inheritdoc />
    IScreenCapture IScreenCaptureService.GetScreenCapture(Display display) => GetScreenCapture(display);

    /// <inheritdoc cref="IScreenCaptureService.GetScreenCapture"/>
    /// <remarks>
    /// The service creates one capture per display and returns it again for every display with the same <see cref="Display.Id"/>, also if other values of the display changed.
    /// A capture that was disposed is replaced by a new one.
    /// A new capture gets the <see cref="Options"/> of the service; an existing one is returned with the options it was created with.
    /// </remarks>
    public X11ScreenCapture GetScreenCapture(Display display) => _screenCaptures.GetOrCreate(display, Options, requireEqualOptions: false);

    /// <summary>
    /// Creates a <see cref="X11ScreenCapture"/> with the given options for the given display.
    /// </summary>
    /// <remarks>
    /// The service creates one capture per display and returns it again for every display with the same <see cref="Display.Id"/>, also if other values of the display changed.
    /// A capture that was disposed is replaced by a new one.
    /// An existing capture is only returned if it was created with equal options.
    /// </remarks>
    /// <param name="display">The display to duplicate.</param>
    /// <param name="options">The options of the capture.</param>
    /// <returns>The <see cref="X11ScreenCapture"/> for the given display.</returns>
    /// <exception cref="InvalidOperationException">The capture of the display already exists with other options.</exception>
    public X11ScreenCapture GetScreenCapture(Display display, X11CaptureOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return _screenCaptures.GetOrCreate(display, options, requireEqualOptions: true);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Disposes all captures created by the service. It's thread-safe and can be called more than once.
    /// </remarks>
    public void Dispose() => _screenCaptures.DisposeAll();

    #endregion
}