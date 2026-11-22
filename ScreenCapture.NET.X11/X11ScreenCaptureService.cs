using System;
using System.Collections.Generic;
using System.Runtime.Versioning;

namespace ScreenCapture.NET;

/// <summary>
/// Represents a <see cref="IScreenCaptureService"/> using the <see cref="X11ScreenCapture"/>.
/// </summary>
/// <remarks>
/// The first connection installs process-wide Xlib error handlers, see <see cref="X11ScreenCapture"/>.
/// A connection that breaks during an enumeration makes it throw. With libX11 older than 1.7, Xlib terminates the process then.
/// <para>Captures get the <see cref="Options"/> of the service, or the ones passed to <see cref="GetScreenCapture(Display, X11CaptureOptions)"/>.</para>
/// <para>
/// In a Wayland session $DISPLAY is usually XWayland, which doesn't show the Wayland desktop: captures fail or are black. Use ScreenCapture.NET.Wayland there.
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
    /// Gets the name of the X display the service and its captures connect to (e.g. <c>:0</c>); <c>null</c> for $DISPLAY.
    /// </summary>
    public string? DisplayName { get; }

    #endregion

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="X11ScreenCaptureService"/> class.
    /// </summary>
    /// <remarks>
    /// Connections are opened when needed, so a display name that can't be opened only throws on enumeration.
    /// </remarks>
    /// <param name="options">The options of captures created without their own options; <c>null</c> for <see cref="X11CaptureOptions.Default"/>.</param>
    /// <param name="displayName">The name of the X display to connect to (e.g. <c>:0</c> or <c>remote:10.0</c>); <c>null</c> for $DISPLAY.</param>
    public X11ScreenCaptureService(X11CaptureOptions? options = null, string? displayName = null)
    {
        Options = options ?? X11CaptureOptions.Default;
        DisplayName = displayName;
        _screenCaptures = new ScreenCaptureCache<X11ScreenCapture, X11CaptureOptions>(this, (display, captureOptions) => new X11ScreenCapture(display, captureOptions, DisplayName), capture => capture.Options);
    }

    #endregion

    #region Methods

    /// <summary>
    /// Checks if the desktop of an X server can be captured: libX11 is installed, a connection to the server can be opened, and the server isn't XWayland.
    /// </summary>
    /// <remarks>
    /// Installs the Xlib error handlers like every connection of this library.
    /// </remarks>
    /// <param name="displayName">The name of the X display, like the one passed to the constructor; <c>null</c> for $DISPLAY.</param>
    /// <returns><c>true</c> if it's supported; otherwise, <c>false</c>.</returns>
    [SupportedOSPlatformGuard("linux")]
    public static bool IsSupported(string? displayName = null)
    {
        if (!OperatingSystem.IsLinux()) return false;

        try
        {
            using X11DisplayHandle display = X11DisplayHandle.Open(displayName);
            return !display.IsInvalid && !X11Displays.IsXWayland(display);
        }
        catch (Exception)
        {
            // E.g. libX11 isn't installed, or the connection broke meanwhile.
            return false;
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// X11 doesn't tell the graphics card, so the X server is the only one.
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
    /// The displays are the RandR 1.5 monitors (<c>xrandr --listmonitors</c>), with their position on the X screen.
    /// A screen without monitors (or without RandR 1.5 or libXrandr) is one display.
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

    // Enumerations are eager, so the connection is closed right away and a missing X server throws.
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
    /// A new capture gets the <see cref="Options"/> of the service, an existing one keeps its own.
    /// </remarks>
    public X11ScreenCapture GetScreenCapture(Display display) => _screenCaptures.GetOrCreate(display, Options, requireEqualOptions: false);

    /// <summary>
    /// Creates a <see cref="X11ScreenCapture"/> with the given options for the given display.
    /// </summary>
    /// <remarks>
    /// There's one capture per <see cref="Display.Id"/>. An existing capture is only returned if it was created with equal options.
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
    /// Disposes all captures created by the service. Can be called more than once.
    /// </remarks>
    public void Dispose() => _screenCaptures.DisposeAll();

    #endregion
}