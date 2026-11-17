using HPPH;
using Vortice.Mathematics;

namespace ScreenCapture.NET;

/// <summary>
/// Maps capture zones, which are in the orientation of the display, to the duplicated texture, which is in the native orientation of the output.
/// </summary>
/// <remarks>
/// The mapping is the inverse of <see cref="CopyRotateHelper"/>: a pixel at (x, y) on a display of size width x height is read from the texture at
/// <list type="bullet">
/// <item><see cref="ImageRotation.None"/>: (x, y)</item>
/// <item><see cref="ImageRotation.Rotate90"/>: (y, width - 1 - x)</item>
/// <item><see cref="ImageRotation.Rotate180"/>: (width - 1 - x, height - 1 - y)</item>
/// <item><see cref="ImageRotation.Rotate270"/>: (height - 1 - y, x)</item>
/// </list>
/// </remarks>
internal static class DX11ZoneLayout
{
    #region Methods

    /// <summary>
    /// Calculates the region of the duplicated texture a zone is copied from.
    /// </summary>
    /// <param name="rotation">The rotation of the display.</param>
    /// <param name="displayWidth">The width of the display (display orientation).</param>
    /// <param name="displayHeight">The height of the display (display orientation).</param>
    /// <param name="x">The x-location of the zone on the display.</param>
    /// <param name="y">The y-location of the zone on the display.</param>
    /// <param name="unscaledWidth">The width of the zone on the display.</param>
    /// <param name="unscaledHeight">The height of the zone on the display.</param>
    /// <returns>The region in texture coordinates.</returns>
    internal static SourceBox GetSourceBox(ImageRotation rotation, int displayWidth, int displayHeight, int x, int y, int unscaledWidth, int unscaledHeight)
        => rotation switch
        {
            ImageRotation.Rotate90 => new SourceBox(y, displayWidth - x - unscaledWidth, unscaledHeight, unscaledWidth),
            ImageRotation.Rotate180 => new SourceBox(displayWidth - x - unscaledWidth, displayHeight - y - unscaledHeight, unscaledWidth, unscaledHeight),
            ImageRotation.Rotate270 => new SourceBox(displayHeight - y - unscaledHeight, x, unscaledHeight, unscaledWidth),
            _ => new SourceBox(x, y, unscaledWidth, unscaledHeight)
        };

    /// <summary>
    /// Converts a size on the display to the size of a texture in the native orientation of the output.
    /// </summary>
    /// <param name="rotation">The rotation of the display.</param>
    /// <param name="width">The width on the display.</param>
    /// <param name="height">The height on the display.</param>
    /// <returns>The size in texture coordinates.</returns>
    internal static (int width, int height) GetTextureSize(ImageRotation rotation, int width, int height)
        => rotation is ImageRotation.Rotate90 or ImageRotation.Rotate270 ? (height, width) : (width, height);

    #endregion
}

/// <summary>
/// A region of the duplicated texture, in texture coordinates.
/// </summary>
/// <param name="X">The x-location of the region.</param>
/// <param name="Y">The y-location of the region.</param>
/// <param name="Width">The width of the region.</param>
/// <param name="Height">The height of the region.</param>
internal readonly record struct SourceBox(int X, int Y, int Width, int Height)
{
    /// <summary>
    /// Converts the region to the <see cref="Box"/> used by <c>CopySubresourceRegion</c>.
    /// </summary>
    /// <returns>The box.</returns>
    public Box ToBox() => new(X, Y, 0, X + Width, Y + Height, 1);
}
