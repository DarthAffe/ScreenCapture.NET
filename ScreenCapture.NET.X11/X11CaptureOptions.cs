namespace ScreenCapture.NET;

/// <summary>
/// Options of a <see cref="X11ScreenCapture"/>.
/// </summary>
/// <remarks>
/// Passed to the <see cref="X11ScreenCaptureService"/> for all its captures, or to <see cref="X11ScreenCaptureService.GetScreenCapture(Display, X11CaptureOptions)"/> for one capture.
/// Two options are equal if all their values are.
/// The X server is chosen with the display name of the <see cref="X11ScreenCaptureService"/>, since it also decides which displays the service enumerates.
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
    /// The X server then writes the pixels straight into memory it shares with this process, which is many times faster than sending them over the connection (<c>XGetSubImage</c>).
    /// It needs libXext and a server on the same machine that can access this process's System V shared memory.
    /// Where it can't (e.g. a remote server, or a server outside the IPC namespace of a container or sandbox), the capture reads the screen without shared memory, without reporting an error.
    /// </remarks>
    public bool UseSharedMemory { get; init; } = true;

    #endregion
}
