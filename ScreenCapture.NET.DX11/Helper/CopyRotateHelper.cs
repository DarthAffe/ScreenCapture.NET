using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using HPPH;

namespace ScreenCapture.NET;

/// <summary>
/// Copies a mapped zone texture into the zone buffer and applies the display rotation.
/// </summary>
/// <remarks>
/// <c>width</c> and <c>height</c> are always the size of the zone (the target).
/// For <see cref="Rotation.Rotate90"/> and <see cref="Rotation.Rotate270"/> the source has <c>width</c> rows of <c>height</c> pixels each.
/// The target is written without padding (stride <c>width * 4</c>).
/// </remarks>
internal static class CopyRotateHelper
{
    #region Methods

    /// <summary>
    /// Copies <paramref name="source"/> into <paramref name="target"/> and applies <paramref name="rotation"/>.
    /// </summary>
    /// <param name="rotation">The rotation of the display.</param>
    /// <param name="source">The mapped texture data.</param>
    /// <param name="sourceStride">The row pitch of <paramref name="source"/> in bytes.</param>
    /// <param name="width">The width of the zone.</param>
    /// <param name="height">The height of the zone.</param>
    /// <param name="target">The zone buffer.</param>
    internal static void Copy(Rotation rotation, ReadOnlySpan<byte> source, int sourceStride, int width, int height, Span<byte> target)
    {
        switch (rotation)
        {
            case Rotation.Rotate90:
                CopyRotate90(source, sourceStride, width, height, target);
                break;

            case Rotation.Rotate180:
                CopyRotate180(source, sourceStride, width, height, target);
                break;

            case Rotation.Rotate270:
                CopyRotate270(source, sourceStride, width, height, target);
                break;

            default:
                CopyRotate0(source, sourceStride, width, height, target);
                break;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void CopyRotate0(ReadOnlySpan<byte> source, int sourceStride, int width, int height, Span<byte> target)
    {
        int stride = width * ColorBGRA.ColorFormat.BytesPerPixel;

        for (int y = 0; y < height; y++)
        {
            int sourceOffset = y * sourceStride;
            int targetOffset = y * stride;

            source.Slice(sourceOffset, stride).CopyTo(target.Slice(targetOffset, stride));
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void CopyRotate90(ReadOnlySpan<byte> source, int sourceStride, int width, int height, Span<byte> target)
    {
        int usedBytesPerLine = height * ColorBGRA.ColorFormat.BytesPerPixel;
        Span<ColorBGRA> pixels = MemoryMarshal.Cast<byte, ColorBGRA>(target);

        for (int x = 0; x < width; x++)
        {
            ReadOnlySpan<ColorBGRA> src = MemoryMarshal.Cast<byte, ColorBGRA>(source.Slice(x * sourceStride, usedBytesPerLine));
            for (int y = 0; y < src.Length; y++)
                pixels[(y * width) + (width - x - 1)] = src[y];
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void CopyRotate180(ReadOnlySpan<byte> source, int sourceStride, int width, int height, Span<byte> target)
    {
        int usedBytesPerLine = width * ColorBGRA.ColorFormat.BytesPerPixel;
        Span<ColorBGRA> pixels = MemoryMarshal.Cast<byte, ColorBGRA>(target);

        for (int y = 0; y < height; y++)
        {
            ReadOnlySpan<ColorBGRA> src = MemoryMarshal.Cast<byte, ColorBGRA>(source.Slice(y * sourceStride, usedBytesPerLine));
            for (int x = 0; x < src.Length; x++)
                pixels[((height - y - 1) * width) + (width - x - 1)] = src[x];
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void CopyRotate270(ReadOnlySpan<byte> source, int sourceStride, int width, int height, Span<byte> target)
    {
        int usedBytesPerLine = height * ColorBGRA.ColorFormat.BytesPerPixel;
        Span<ColorBGRA> pixels = MemoryMarshal.Cast<byte, ColorBGRA>(target);

        for (int x = 0; x < width; x++)
        {
            ReadOnlySpan<ColorBGRA> src = MemoryMarshal.Cast<byte, ColorBGRA>(source.Slice(x * sourceStride, usedBytesPerLine));
            for (int y = 0; y < src.Length; y++)
                pixels[((height - y - 1) * width) + x] = src[y];
        }
    }

    #endregion
}
