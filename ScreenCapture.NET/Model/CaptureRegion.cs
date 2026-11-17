// ReSharper disable MemberCanBePrivate.Global

namespace ScreenCapture.NET;

/// <summary>
/// Represents the region of a display captured by a <see cref="ICaptureZone"/>, in display pixels (before downscaling).
/// </summary>
/// <remarks>
/// Change a single value of the region of a zone with <c>with</c>:
/// <code>
/// screenCapture.UpdateCaptureZone(zone, zone.Region with { X = 100 });
/// </code>
/// </remarks>
/// <param name="X">The x-location of the region on the display.</param>
/// <param name="Y">The y-location of the region on the display.</param>
/// <param name="Width">The width of the region.</param>
/// <param name="Height">The height of the region.</param>
public readonly record struct CaptureRegion(int X, int Y, int Width, int Height)
{
    /// <summary>
    /// Checks if the region isn't empty and lies within an area of the given size (e.g. a display).
    /// </summary>
    /// <param name="width">The width of the area.</param>
    /// <param name="height">The height of the area.</param>
    /// <returns><c>true</c> if <see cref="X"/> and <see cref="Y"/> aren't negative, <see cref="Width"/> and <see cref="Height"/> are greater than 0 and the region ends within the area; otherwise <c>false</c>.</returns>
    public bool IsWithin(int width, int height)
        => (X >= 0) && (Y >= 0) && (Width > 0) && (Height > 0) && (Width <= (width - X)) && (Height <= (height - Y));
}