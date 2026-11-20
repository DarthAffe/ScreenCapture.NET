using System;
using System.Collections.Generic;

namespace ScreenCapture.NET;

/// <summary>
/// Represents a <see cref="IScreenCaptureService"/> using the <see cref="X11ScreenCapture"/>.
/// </summary>
/// <remarks>
/// The first connection to the X server installs process-wide Xlib error handlers that stay installed: one for errors of requests (<c>XSetErrorHandler</c>)
/// and, with libX11 1.7 or newer, one for broken connections (<c>XSetIOErrorHandler</c>).
/// They handle the errors of this library's connections instead of Xlib terminating the process, and pass the errors of all other connections to the handlers that were installed before.
/// If other code installs its own handlers afterwards without passing errors on, the errors of the captures and the enumeration are handled by that code.
/// <para>
/// A connection that breaks during an enumeration (e.g. the X server stops) makes it throw; how captures handle it is described on <see cref="X11ScreenCapture"/>.
/// With a libX11 older than 1.7, Xlib terminates the process when a connection breaks.
/// </para>
/// <para>Captures get the <see cref="Options"/> of the service, or the ones passed to <see cref="GetScreenCapture(Display, X11CaptureOptions)"/>.</para>
/// <para>
/// It captures X11 sessions. In a Wayland session, $DISPLAY is usually XWayland, whose root window doesn't show the Wayland desktop (only the windows of X clients, if anything):
/// captures there fail or are black. Use ScreenCapture.NET.Wayland for Wayland desktops.
/// </para>
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
    /// <remarks>
    /// X11 doesn't tell which graphics card shows a screen, so the X server is the only graphics card.
    /// </remarks>
    /// <exception cref="InvalidOperationException">No connection to an X server could be opened.</exception>
    public IEnumerable<GraphicsCard> GetGraphicsCards()
    {
        ObjectDisposedException.ThrowIf(_screenCaptures.IsDisposed, this);

        using X11DisplayHandle display = OpenDisplay();
        return [X11Displays.GetGraphicsCard(display)];
    }

    /// <inheritdoc />
    /// <remarks>
    /// The displays are the active monitors of the X server as RandR 1.5 reports them (what <c>xrandr --listmonitors</c> shows, incl. monitors defined with <c>xrandr --setmonitor</c>),
    /// with their position on the X screen. A screen without monitors (or a server without RandR 1.5 or without libXrandr) is one display.
    /// </remarks>
    /// <exception cref="InvalidOperationException">No connection to an X server could be opened, or it broke during the enumeration.</exception>
    public IEnumerable<Display> GetDisplays(GraphicsCard graphicsCard)
    {
        ObjectDisposedException.ThrowIf(_screenCaptures.IsDisposed, this);

        using X11DisplayHandle display = OpenDisplay();
        if (!string.Equals(graphicsCard.Id, X11Displays.GetGraphicsCard(display).Id, StringComparison.Ordinal)) return [];

        List<X11Display> displays = X11Displays.GetDisplays(display);
        List<Display> result = new(displays.Count);
        foreach (X11Display x11Display in displays)
            result.Add(x11Display.Display);

        return result;
    }

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