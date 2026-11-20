namespace ScreenCapture.NET;

/// <summary>
/// Options of the screen cast session of a <see cref="WaylandScreenCaptureService"/>: they apply to all its captures.
/// </summary>
/// <remarks>
/// The portal fixes them when the session starts (the first <see cref="WaylandScreenCaptureService.GetDisplays"/>, or the first one after the session ended).
/// Two options are equal if all their values are.
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
    /// The compositor draws it into the frames (so it's averaged into downscaled zones like everything else), and a pointer that moves makes a new frame.
    /// It needs a portal that offers it (version 2 of the ScreenCast interface and the "embedded" cursor mode); otherwise the pointer isn't drawn.
    /// Without it, the pointer isn't drawn either (unless the portal can't hide it).
    /// </remarks>
    public bool DrawCursor { get; init; }

    /// <summary>
    /// Gets the token of an earlier session to restore (see <see cref="WaylandScreenCaptureService.RestoreToken"/>): its monitors are shared again without the dialog. (default <c>null</c>)
    /// </summary>
    /// <remarks>
    /// The portal shows the dialog if it can't restore the session (e.g. the token is unknown, was used already, or the user revoked the permission).
    /// A token comes from a session started with a <see cref="PersistMode"/> and is often only valid once: GNOME hands out a new one with every session, store the latest.
    /// Needs version 4 of the ScreenCast portal (ignored by older ones).
    /// </remarks>
    public string? RestoreToken { get; init; }

    /// <summary>
    /// Gets how long the portal remembers the monitors the user shares, so a later session with the <see cref="WaylandScreenCaptureService.RestoreToken"/> starts without the dialog.
    /// (default <see cref="WaylandPersistMode.Persistent"/>)
    /// </summary>
    /// <remarks>
    /// Needs version 4 of the ScreenCast portal (older ones don't remember anything). The compositor may let the user decide in the dialog (e.g. KDE with a checkbox).
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
    /// Nothing is remembered: every session shows the dialog.
    /// </summary>
    None = 0,

    /// <summary>
    /// Until the process ends: a session that starts after the last one ended (see <see cref="WaylandScreenCaptureService.GetDisplays"/>) is restored without the dialog.
    /// </summary>
    WhileRunning = 1,

    /// <summary>
    /// Until the user revokes it: a session started with the <see cref="WaylandScreenCaptureService.RestoreToken"/>, also by a later process, is restored without the dialog.
    /// </summary>
    Persistent = 2
}
