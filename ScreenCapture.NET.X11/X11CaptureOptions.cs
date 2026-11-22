namespace ScreenCapture.NET;

/// <summary>
/// Options of a <see cref="X11ScreenCapture"/>.
/// </summary>
/// <remarks>
/// Passed to the <see cref="X11ScreenCaptureService"/> for all its captures, or to <see cref="X11ScreenCaptureService.GetScreenCapture(Display, X11CaptureOptions)"/> for one.
/// The X server is chosen with the display name of the service.
/// </remarks>
public sealed record X11CaptureOptions
{
    #region Properties & Fields

    /// <summary>
    /// Gets the default options.
    /// </summary>
    public static X11CaptureOptions Default { get; } = new();

    /// <summary>
    /// Gets a value indicating whether the screen is read through shared memory (the MIT-SHM extension). (default <c>true</c>)
    /// </summary>
    /// <remarks>
    /// Many times faster than sending the pixels over the connection. Needs libXext and a local server that can access the System V shared memory of this process.
    /// Where it can't (e.g. a remote server, or a container or sandbox), the capture silently reads without it.
    /// </remarks>
    public bool UseSharedMemory { get; init; } = true;

    #endregion
}
