using System;

namespace ScreenCapture.NET;

/// <summary>
/// Options of a <see cref="WGCScreenCapture"/>.
/// </summary>
/// <remarks>
/// Passed to the <see cref="WGCScreenCaptureService"/> for all its captures, or to <see cref="WGCScreenCaptureService.GetScreenCapture(Display, WGCCaptureOptions)"/> for one.
/// </remarks>
// ReSharper disable once InconsistentNaming
public sealed record WGCCaptureOptions
{
    #region Properties & Fields

    /// <summary>
    /// Gets the longest supported <see cref="Timeout"/> (<see cref="int.MaxValue"/> milliseconds, about 24.8 days).
    /// </summary>
    public static TimeSpan MaxTimeout => CaptureTimeout.Max;

    /// <summary>
    /// Gets the default options.
    /// </summary>
    public static WGCCaptureOptions Default { get; } = new();

    /// <summary>
    /// Gets how long <see cref="IScreenCapture.CaptureScreen"/> waits for a new frame before it returns <see cref="CaptureResult.Timeout"/>. (default 1 s)
    /// </summary>
    /// <remarks>
    /// Rounded up to whole milliseconds; 0 doesn't wait. Disposing a capture can take up to this long.
    /// Can be changed later with <see cref="WGCScreenCapture.Timeout"/>.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative (including <see cref="System.Threading.Timeout.InfiniteTimeSpan"/>) or greater than <see cref="MaxTimeout"/>.</exception>
    public TimeSpan Timeout
    {
        get;
        init => field = CaptureTimeout.Validate(value);
    } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Gets if Windows draws a border around the captured display. (default <c>false</c>)
    /// </summary>
    /// <remarks>
    /// It isn't part of the frames, but other captures and the user see it. Windows 10 always draws it.
    /// </remarks>
    public bool ShowBorder { get; init; }

    /// <summary>
    /// Gets a value indicating whether the mouse pointer is part of the frames. (default <c>false</c>)
    /// </summary>
    /// <remarks>
    /// A pointer that moves or changes its shape makes a new frame. Before Windows 10 2004 the pointer is always drawn.
    /// </remarks>
    public bool DrawCursor { get; init; }

    /// <summary>
    /// Gets a value indicating whether the displays are captured in HDR and tone mapped to the 8-bit zones. (default <c>false</c>)
    /// </summary>
    /// <remarks>
    /// Without it, Windows converts HDR to 8 bits itself.
    /// With it, the 16-bit image is tone mapped on the GPU: colors up to SDR white (the "SDR content brightness" setting) look like on an SDR display,
    /// brighter ones are scaled down keeping their hue. Displays without HDR look the same, but cost some GPU time per frame.
    /// </remarks>
    public bool CaptureHdr { get; init; }

    #endregion
}
