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

    #endregion
}
