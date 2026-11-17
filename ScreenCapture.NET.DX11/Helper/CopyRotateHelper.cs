using System;
using HPPH;

namespace ScreenCapture.NET;

/// <summary>
/// Copies a mapped zone texture into the zone buffer and applies the display rotation.
/// </summary>
/// <remarks>
/// <c>width</c> and <c>height</c> are always the size of the zone (the target).
/// For <see cref="ImageRotation.Rotate90"/> and <see cref="ImageRotation.Rotate270"/> the source has <c>width</c> rows of <c>height</c> pixels each.
/// The target is written without padding (stride <c>width * 4</c>).
/// </remarks>
internal static class CopyRotateHelper
{
    #region Methods

    /// <summary>
    /// Copies <paramref name="source"/> into <paramref name="target"/> and applies <paramref name="rotation"/>.
    /// </summary>
    /// <param name="rotation">The rotation of the display (see <see cref="Display.Rotation"/>): the texture, in the orientation of the output, is rotated by it clockwise.</param>
    /// <param name="source">The mapped texture data.</param>
    /// <param name="sourceStride">The row pitch of <paramref name="source"/> in bytes.</param>
    /// <param name="width">The width of the zone.</param>
    /// <param name="height">The height of the zone.</param>
    /// <param name="target">The zone buffer.</param>
    internal static void Copy(ImageRotation rotation, ReadOnlySpan<byte> source, int sourceStride, int width, int height, Span<byte> target)
    {
        (int textureWidth, int textureHeight) = PixelHelper.GetRotatedSize(width, height, rotation);

        RefImage<ColorBGRA>.Wrap(source, textureWidth, textureHeight, sourceStride)
                           .Rotate(rotation, target, width * ColorBGRA.ColorFormat.BytesPerPixel);
    }

    #endregion
}
