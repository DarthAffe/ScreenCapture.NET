namespace ScreenCapture.NET;

/// <summary>
/// Options of the screen cast session of a <see cref="WaylandScreenCaptureService"/>, for all its captures.
/// </summary>
/// <remarks>
/// The portal fixes them when the session starts.
/// </remarks>
public sealed record WaylandSessionOptions
{
    #region Properties & Fields

    /// <summary>
    /// Gets the default options.
    /// </summary>
    public static WaylandSessionOptions Default { get; } = new();

    /// <summary>
    /// Gets a value indicating whether the mouse pointer is part of the frames. (default <c>false</c>)
    /// </summary>
    /// <remarks>
    /// A pointer that moves makes a new frame. Needs a portal with the "embedded" cursor mode (ScreenCast version 2), otherwise it isn't drawn.
    /// </remarks>
    public bool DrawCursor { get; init; }

    /// <summary>
    /// Gets the token of an earlier session to restore without the dialog (see <see cref="WaylandScreenCaptureService.RestoreToken"/>). (default <c>null</c>)
    /// </summary>
    /// <remarks>
    /// The portal shows the dialog if it can't restore (e.g. the token is unknown or used, or the user revoked the permission).
    /// Tokens are often only valid once (GNOME hands out a new one with every session), so store the latest. Needs ScreenCast version 4.
    /// </remarks>
    public string? RestoreToken { get; init; }

    /// <summary>
    /// Gets how long the portal remembers the shared monitors for a later session with the <see cref="WaylandScreenCaptureService.RestoreToken"/>.
    /// (default <see cref="WaylandPersistMode.Persistent"/>)
    /// </summary>
    /// <remarks>
    /// Needs ScreenCast version 4. The compositor may let the user decide in the dialog (e.g. KDE with a checkbox).
    /// </remarks>
    public WaylandPersistMode PersistMode { get; init; } = WaylandPersistMode.Persistent;

    #endregion
}

/// <summary>
/// How long the ScreenCast portal remembers the monitors a user shared (<c>persist_mode</c>).
/// </summary>
public enum WaylandPersistMode
{
    /// <summary>
    /// Nothing is remembered, every session shows the dialog.
    /// </summary>
    None = 0,

    /// <summary>
    /// Until the process ends.
    /// </summary>
    WhileRunning = 1,

    /// <summary>
    /// Until the user revokes it, also for later processes.
    /// </summary>
    Persistent = 2
}
