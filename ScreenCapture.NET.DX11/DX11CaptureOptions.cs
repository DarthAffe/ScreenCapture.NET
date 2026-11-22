using System;

namespace ScreenCapture.NET;

/// <summary>
/// Options of a <see cref="DX11ScreenCapture"/>.
/// </summary>
/// <remarks>
/// Passed to the <see cref="DX11ScreenCaptureService"/> for all its captures, or to <see cref="DX11ScreenCaptureService.GetScreenCapture(Display, DX11CaptureOptions)"/> for one.
/// </remarks>
public sealed record DX11CaptureOptions
{
    #region Properties & Fields

    /// <summary>
    /// Gets the longest supported <see cref="Timeout"/> (<see cref="int.MaxValue"/> milliseconds, about 24.8 days).
    /// </summary>
    public static TimeSpan MaxTimeout => CaptureTimeout.Max;

    /// <summary>
    /// Gets the default options.
    /// </summary>
    public static DX11CaptureOptions Default { get; } = new();

    /// <summary>
    /// Gets how long <see cref="IScreenCapture.CaptureScreen"/> waits for a new frame before it returns <see cref="CaptureResult.Timeout"/>. (default 1 s)
    /// </summary>
    /// <remarks>
    /// Rounded up to whole milliseconds; 0 doesn't wait. Disposing a capture can take up to this long.
    /// Can be changed later with <see cref="DX11ScreenCapture.Timeout"/>.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative (including <see cref="System.Threading.Timeout.InfiniteTimeSpan"/>) or greater than <see cref="MaxTimeout"/>.</exception>
    public TimeSpan Timeout
    {
        get;
        init => field = CaptureTimeout.Validate(value);
    } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Gets a value indicating whether a display showing HDR is captured in HDR and tone mapped to the 8-bit zones. (default <c>false</c>)
    /// </summary>
    /// <remarks>
    /// Without it, Windows converts HDR to 8 bits itself and the capture is far too bright and washed out.
    /// With it, the 16-bit image is tone mapped on the GPU: colors up to SDR white (the "SDR content brightness" setting) look like on an SDR display,
    /// brighter ones are scaled down keeping their hue. Displays without HDR aren't affected.
    /// <para>
    /// Uses <c>IDXGIOutput5::DuplicateOutput1</c>, which needs a per-monitor DPI aware (v2) process (see <see cref="DPIAwareness.Initialize"/>) and doesn't work on a WPF thread.
    /// Otherwise the capture is <see cref="CaptureResult.Unavailable"/> and reports the error.
    /// </para>
    /// </remarks>
    public bool CaptureHdr { get; init; }

    /// <summary>
    /// Gets a value indicating whether the mouse pointer is drawn into the zones. (default <c>false</c>)
    /// </summary>
    /// <remarks>
    /// Desktop Duplication delivers the pointer separately, it's blended into the zones when they're written.
    /// A pointer change counts as a new frame (<see cref="CaptureResult.NewFrame"/>), also if nothing else changed.
    /// </remarks>
    public bool DrawCursor { get; init; }

    #endregion
}
