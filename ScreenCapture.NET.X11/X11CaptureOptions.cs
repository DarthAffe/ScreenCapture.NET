namespace ScreenCapture.NET;

/// <summary>
/// Options of a <see cref="X11ScreenCapture"/>.
/// </summary>
/// <remarks>
/// Passed to the <see cref="X11ScreenCaptureService"/> for all its captures, or to <see cref="X11ScreenCaptureService.GetScreenCapture(Display, X11CaptureOptions)"/> for one capture.
/// Two options are equal if all their values are.
/// There are no options yet; the type exists so options can be added without changing the API of the service.
/// The X server is chosen with the display name of the <see cref="X11ScreenCaptureService"/>, since it also decides which displays the service enumerates.
/// </remarks>
public sealed record X11CaptureOptions
{
    #region Properties & Fields

    /// <summary>
    /// Gets the default options.
    /// </summary>
    public static X11CaptureOptions Default { get; } = new();

    #endregion
}
