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
public readonly record struct CaptureRegion(int X, int Y, int Width, int Height);