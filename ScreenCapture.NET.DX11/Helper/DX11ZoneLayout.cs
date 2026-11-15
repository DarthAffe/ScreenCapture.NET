using Vortice.Mathematics;

namespace ScreenCapture.NET;

/// <summary>
/// Maps capture zones, which are in the orientation of the display, to the duplicated texture, which is in the native orientation of the output.
/// </summary>
internal static class DX11ZoneLayout
{
    #region Methods

    /// <summary>
    /// Calculates the region of the duplicated texture a zone is copied from.
    /// </summary>
    /// <remarks>
    /// For displays rotated by 90 or 270 degrees the axes are swapped.
    /// </remarks>
    /// <param name="rotation">The rotation of the display.</param>
    /// <param name="x">The x-location of the zone on the display.</param>
    /// <param name="y">The y-location of the zone on the display.</param>
    /// <param name="unscaledWidth">The width of the zone on the display.</param>
    /// <param name="unscaledHeight">The height of the zone on the display.</param>
    /// <returns>The region in texture coordinates.</returns>
    internal static SourceBox GetSourceBox(Rotation rotation, int x, int y, int unscaledWidth, int unscaledHeight)
        => IsSwapped(rotation)
               ? new SourceBox(y, x, unscaledHeight, unscaledWidth)
               : new SourceBox(x, y, unscaledWidth, unscaledHeight);

    /// <summary>
    /// Converts a size on the display to the size of a texture in the native orientation of the output.
    /// </summary>
    /// <param name="rotation">The rotation of the display.</param>
    /// <param name="width">The width on the display.</param>
    /// <param name="height">The height on the display.</param>
    /// <returns>The size in texture coordinates.</returns>
    internal static (int width, int height) GetTextureSize(Rotation rotation, int width, int height)
        => IsSwapped(rotation) ? (height, width) : (width, height);

    private static bool IsSwapped(Rotation rotation) => rotation is Rotation.Rotate90 or Rotation.Rotate270;

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
