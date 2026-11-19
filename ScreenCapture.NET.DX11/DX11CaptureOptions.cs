using System;

namespace ScreenCapture.NET;

/// <summary>
/// Options of a <see cref="DX11ScreenCapture"/>.
/// </summary>
/// <remarks>
/// Passed to the <see cref="DX11ScreenCaptureService"/> for all its captures, or to <see cref="DX11ScreenCaptureService.GetScreenCapture(Display, DX11CaptureOptions)"/> for one capture.
/// Two options are equal if all their values are.
/// </remarks>
public sealed record DX11CaptureOptions
{
    #region Properties & Fields

    /// <summary>
    /// Gets the longest supported <see cref="Timeout"/> (<see cref="int.MaxValue"/> milliseconds, about 24.8 days).
    /// </summary>
    public static TimeSpan MaxTimeout { get; } = TimeSpan.FromMilliseconds(int.MaxValue);

    /// <summary>
    /// Gets the default options.
    /// </summary>
    public static DX11CaptureOptions Default { get; } = new();

    /// <summary>
    /// Gets how long <see cref="IScreenCapture.CaptureScreen"/> waits for a new frame before it returns <see cref="CaptureResult.Timeout"/>. (default 1 s)
    /// </summary>
    /// <remarks>
    /// It's rounded up to whole milliseconds; 0 doesn't wait. Disposing a capture waits for a running <see cref="IScreenCapture.CaptureScreen"/>, so up to this long.
    /// The <see cref="DX11ScreenCapture.Timeout"/> of a capture starts with this value and can be changed while it runs.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative (including <see cref="System.Threading.Timeout.InfiniteTimeSpan"/>) or greater than <see cref="MaxTimeout"/>.</exception>
    public TimeSpan Timeout
    {
        get;
        init => field = ValidateTimeout(value);
    } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Gets a value indicating whether a display showing HDR is captured in HDR and tone mapped to the 8-bit zones. (default <c>false</c>)
    /// </summary>
    /// <remarks>
    /// Without it, Windows converts the HDR image of the display to 8 bits itself, and the capture is far too bright and washed out (SDR white isn't mapped to white).
    /// With it, the capture gets the 16-bit image and maps it on the GPU: colors up to SDR white (the "SDR content brightness" setting of the display)
    /// get the values they have on an SDR display, brighter colors are scaled down as a whole (keeping their hue), and colors outside of the sRGB gamut are clamped to it.
    /// A display that doesn't show HDR is captured as without it.
    /// <para>
    /// It uses <c>IDXGIOutput5::DuplicateOutput1</c>, which needs a per-monitor DPI aware (version 2) process (see <see cref="DPIAwareness.Initialize"/>) and doesn't work on a WPF thread.
    /// If that fails, the capture reports the error (<see cref="IScreenCapture.LastError"/>) and returns <see cref="CaptureResult.Unavailable"/>.
    /// </para>
    /// </remarks>
    public bool CaptureHdr { get; init; }

    /// <summary>
    /// Gets a value indicating whether the mouse pointer is drawn into the zones. (default <c>false</c>)
    /// </summary>
    /// <remarks>
    /// Desktop Duplication delivers the pointer separately from the frame; it's blended into the zones it overlaps when they're written,
    /// averaged over the blocks of downscaled zones.
    /// A pointer that moves, changes its shape, appears or disappears counts as a new frame (<see cref="CaptureResult.NewFrame"/>), also if nothing else changed,
    /// so zones show it at its new place.
    /// </remarks>
    public bool DrawCursor { get; init; }

    #endregion

    #region Methods

    internal static TimeSpan ValidateTimeout(TimeSpan timeout)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(timeout, TimeSpan.Zero, nameof(Timeout));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(timeout, MaxTimeout, nameof(Timeout));

        return timeout;
    }

    /// <summary>
    /// Converts a validated timeout to whole milliseconds, rounded up.
    /// </summary>
    internal static int ToMilliseconds(TimeSpan timeout) => (int)Math.Ceiling(timeout.TotalMilliseconds);

    #endregion
}
