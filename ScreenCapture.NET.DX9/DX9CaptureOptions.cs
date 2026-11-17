namespace ScreenCapture.NET;

/// <summary>
/// Options of a <see cref="DX9ScreenCapture"/>.
/// </summary>
/// <remarks>
/// Passed to the <see cref="DX9ScreenCaptureService"/> for all its captures, or to <see cref="DX9ScreenCaptureService.GetScreenCapture(Display, DX9CaptureOptions)"/> for one capture.
/// Two options are equal if all their values are.
/// There are no options yet; the type exists so options can be added without changing the API of the service.
/// </remarks>
public sealed record DX9CaptureOptions
{
    #region Properties & Fields

    /// <summary>
    /// Gets the default options.
    /// </summary>
    public static DX9CaptureOptions Default { get; } = new();

    #endregion
}
