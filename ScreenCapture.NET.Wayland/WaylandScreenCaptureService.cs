using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ScreenCapture.NET;

/// <summary>
/// Represents a <see cref="IScreenCaptureService"/> that captures the monitors of a Wayland desktop through the ScreenCast portal (xdg-desktop-portal) and PipeWire.
/// </summary>
/// <remarks>
/// Wayland compositors only share the screen with the consent of the user: the first <see cref="GetDisplays"/> starts a screen cast session,
/// for which the compositor shows a dialog where the user chooses the monitors to share. The displays are the shared monitors (see <see cref="GetDisplays"/>).
/// It works wherever xdg-desktop-portal with a backend for the desktop runs (e.g. KDE Plasma, GNOME, or wlroots compositors with xdg-desktop-portal-wlr), and needs libpipewire.
/// <para>
/// The portal can remember the shared monitors (<see cref="WaylandSessionOptions.PersistMode"/>): it returns a <see cref="RestoreToken"/> that starts a later session without the dialog,
/// also in a later process if the app stores it and passes it back (<see cref="WaylandSessionOptions.RestoreToken"/>).
/// </para>
/// <para>Captures get the <see cref="Options"/> of the service, or the ones passed to <see cref="GetScreenCapture(Display, WaylandCaptureOptions)"/>.</para>
/// </remarks>
public sealed class WaylandScreenCaptureService : IScreenCaptureService
{
    #region Properties & Fields

    // Held while a session starts (which waits for the user), so only one session starts at a time.
    private readonly Lock _startLock = new();

    // Held briefly for the state below; Dispose only takes this one, so it doesn't wait for the user.
    private readonly Lock _stateLock = new();
    private ScreenCastSession? _session;
    private volatile bool _isDisposed;
    private volatile string? _restoreToken;

    private readonly ScreenCaptureCache<WaylandScreenCapture, WaylandCaptureOptions> _screenCaptures;

    /// <summary>
    /// Gets the options of captures created without their own options.
    /// </summary>
    public WaylandCaptureOptions Options { get; }

    /// <summary>
    /// Gets the options of the screen cast session, which apply to all captures.
    /// </summary>
    public WaylandSessionOptions SessionOptions { get; }

    /// <summary>
    /// Gets the token to restore the shared monitors in a later session without the dialog: the latest one the portal returned,
    /// or the one of the <see cref="SessionOptions"/> until a session returned one. <c>null</c> if there's none.
    /// </summary>
    /// <remarks>
    /// A new session (after one ended) restores with it on its own. Store it to restore the session in a later process (<see cref="WaylandSessionOptions.RestoreToken"/>):
    /// the portal may hand out a new one with every session (see <see cref="RestoreTokenChanged"/>).
    /// </remarks>
    public string? RestoreToken => _restoreToken;

    #endregion

    #region Events

    /// <summary>
    /// Occurs when a session started and the portal returned another <see cref="RestoreToken"/> than the current one.
    /// </summary>
    /// <remarks>
    /// Raised by <see cref="GetDisplays"/> (where sessions start) on its thread, after the session started and before it returns.
    /// Exceptions thrown by a handler are ignored: <see cref="GetDisplays"/> returns the displays of the session anyway.
    /// </remarks>
    public event EventHandler<WaylandRestoreTokenChangedEventArgs>? RestoreTokenChanged;

    #endregion

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="WaylandScreenCaptureService"/> class.
    /// </summary>
    /// <remarks>
    /// Nothing is connected yet: the screen cast session starts with the first <see cref="GetDisplays"/>.
    /// </remarks>
    /// <param name="options">The options of captures created without their own options; <c>null</c> for <see cref="WaylandCaptureOptions.Default"/>.</param>
    /// <param name="sessionOptions">The options of the screen cast session; <c>null</c> for <see cref="WaylandSessionOptions.Default"/>.</param>
    public WaylandScreenCaptureService(WaylandCaptureOptions? options = null, WaylandSessionOptions? sessionOptions = null)
    {
        Options = options ?? WaylandCaptureOptions.Default;
        SessionOptions = sessionOptions ?? WaylandSessionOptions.Default;
        _restoreToken = SessionOptions.RestoreToken;
        _screenCaptures = new ScreenCaptureCache<WaylandScreenCapture, WaylandCaptureOptions>(this, (display, captureOptions) => new WaylandScreenCapture(this, display, captureOptions), capture => capture.Options);
    }

    #endregion

    #region Methods

    /// <inheritdoc />
    /// <remarks>
    /// The portal doesn't tell which graphics card shows a monitor, so there's one graphics card (id <c>xdg-desktop-portal</c>, named after $XDG_CURRENT_DESKTOP).
    /// It doesn't start a session.
    /// </remarks>
    public IEnumerable<GraphicsCard> GetGraphicsCards()
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        return [WaylandDisplays.GetGraphicsCard()];
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// The displays are the monitors the user shares: the first call starts a screen cast session and <b>blocks until the user answered the dialog of the compositor</b>.
    /// Later calls return the displays of that session without a dialog. If the session ended (e.g. the user stopped sharing), the next call starts a new one,
    /// again with the dialog unless the portal restores it (see <see cref="RestoreToken"/>).
    /// </para>
    /// <para>
    /// The ids of the displays are <c>stream:</c> and the id the portal gives the stream, or its index if the portal gives none (e.g. KDE).
    /// The position is the one of the monitor in the compositor's space if the portal tells it, otherwise 0.
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">The ScreenCast portal isn't available, the user cancelled the dialog, or the portal ended the session before it started.</exception>
    /// <exception cref="ObjectDisposedException">The service was disposed (also while waiting for the user).</exception>
    public IEnumerable<Display> GetDisplays(GraphicsCard graphicsCard)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        GraphicsCard portal = WaylandDisplays.GetGraphicsCard();
        if (!string.Equals(graphicsCard.Id, portal.Id, StringComparison.Ordinal)) return [];

        ScreenCastSession session = GetSession(out string? newRestoreToken);

        // Outside the locks: the handler may call into the service.
        if (newRestoreToken != null)
            try
            {
                RestoreTokenChanged?.Invoke(this, new WaylandRestoreTokenChangedEventArgs(newRestoreToken));
            }
            catch { /* Exceptions of handlers aren't errors of the service: the session started, and the token is in RestoreToken. */ }

        return WaylandDisplays.GetDisplays(session.Streams, portal);
    }

    // Returns the current session, or starts a new one. newRestoreToken is the token a new session returned if it's another than the current one.
    private ScreenCastSession GetSession(out string? newRestoreToken)
    {
        newRestoreToken = null;

        lock (_startLock)
        {
            ScreenCastSession? ended;
            ScreenCastSession session;
            lock (_stateLock)
            {
                ObjectDisposedException.ThrowIf(_isDisposed, this);

                if (_session is { IsClosed: false } current) return current;

                ended = _session;
                _session = session = new ScreenCastSession();
            }

            ended?.Dispose();

            try
            {
                // Started on the thread pool: waiting on it here can't deadlock with a synchronization context of the caller.
                string? restoreToken = _restoreToken;
                Task.Run(() => session.StartAsync(SessionOptions, restoreToken)).GetAwaiter().GetResult();

                if ((session.RestoreToken != null) && !string.Equals(session.RestoreToken, restoreToken, StringComparison.Ordinal))
                    _restoreToken = newRestoreToken = session.RestoreToken;

                return session;
            }
            catch (Exception)
            {
                lock (_stateLock)
                    if (_session == session)
                        _session = null;

                session.Dispose();

                // Dispose ended the session while it started.
                ObjectDisposedException.ThrowIf(_isDisposed, this);
                throw;
            }
        }
    }

    /// <summary>
    /// Finds the stream of a display in the current session.
    /// </summary>
    /// <returns>The session, the stream and its display; <c>null</c> if there's no started session (anymore) or it doesn't share the display.</returns>
    internal (ScreenCastSession Session, PortalStream Stream, Display Display)? FindStream(string displayId)
    {
        ScreenCastSession? session;
        lock (_stateLock)
            session = _session;

        if (session is not { IsStarted: true, IsClosed: false }) return null;

        List<Display> displays = WaylandDisplays.GetDisplays(session.Streams, WaylandDisplays.GetGraphicsCard());
        for (int i = 0; i < displays.Count; i++)
            if (string.Equals(displays[i].Id, displayId, StringComparison.Ordinal))
                return (session, session.Streams[i], displays[i]);

        return null;
    }

    /// <inheritdoc />
    IScreenCapture IScreenCaptureService.GetScreenCapture(Display display) => GetScreenCapture(display);

    /// <inheritdoc cref="IScreenCaptureService.GetScreenCapture"/>
    /// <remarks>
    /// The display has to be one of the current session (see <see cref="GetDisplays"/>); otherwise the capture is <see cref="CaptureResult.Unavailable"/>.
    /// The service creates one capture per display and returns it again for every display with the same <see cref="Display.Id"/>, also if other values of the display changed.
    /// A capture that was disposed is replaced by a new one.
    /// A new capture gets the <see cref="Options"/> of the service; an existing one is returned with the options it was created with.
    /// </remarks>
    public WaylandScreenCapture GetScreenCapture(Display display) => _screenCaptures.GetOrCreate(display, Options, requireEqualOptions: false);

    /// <summary>
    /// Creates a <see cref="WaylandScreenCapture"/> with the given options for the given display.
    /// </summary>
    /// <remarks>
    /// The display has to be one of the current session (see <see cref="GetDisplays"/>); otherwise the capture is <see cref="CaptureResult.Unavailable"/>.
    /// The service creates one capture per display and returns it again for every display with the same <see cref="Display.Id"/>, also if other values of the display changed.
    /// A capture that was disposed is replaced by a new one.
    /// An existing capture is only returned if it was created with equal options.
    /// </remarks>
    /// <param name="display">The display to capture.</param>
    /// <param name="options">The options of the capture.</param>
    /// <returns>The <see cref="WaylandScreenCapture"/> for the given display.</returns>
    /// <exception cref="InvalidOperationException">The capture of the display already exists with other options.</exception>
    public WaylandScreenCapture GetScreenCapture(Display display, WaylandCaptureOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return _screenCaptures.GetOrCreate(display, options, requireEqualOptions: true);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Disposes all captures created by the service and ends the screen cast session; a <see cref="GetDisplays"/> waiting for the user then throws an <see cref="ObjectDisposedException"/>.
    /// It's thread-safe and can be called more than once.
    /// </remarks>
    public void Dispose()
    {
        ScreenCastSession? session;
        lock (_stateLock)
        {
            if (_isDisposed) return;

            _isDisposed = true;
            session = _session;
            _session = null;
        }

        _screenCaptures.DisposeAll();
        session?.Dispose();
    }

    #endregion
}
