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
/// It works wherever xdg-desktop-portal with a backend for the desktop runs (e.g. KDE Plasma, GNOME, or wlroots compositors with xdg-desktop-portal-wlr).
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
    /// Later calls return the displays of that session without a dialog. If the session ended (e.g. the user stopped sharing), the next call starts a new one, again with the dialog.
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

        return WaylandDisplays.GetDisplays(GetSession().Streams, portal);
    }

    private ScreenCastSession GetSession()
    {
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
                Task.Run(session.StartAsync).GetAwaiter().GetResult();
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

    /// <inheritdoc />
    public IScreenCapture GetScreenCapture(Display display) => throw new NotImplementedException();

    /// <inheritdoc />
    /// <remarks>
    /// Ends the screen cast session; a <see cref="GetDisplays"/> waiting for the user then throws an <see cref="ObjectDisposedException"/>.
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

        session?.Dispose();
    }

    #endregion
}
