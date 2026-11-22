using System;
using System.Buffers;
using HPPH;

namespace ScreenCapture.NET;

/// <summary>
/// Writes the pixels of capture zones in software, for backends whose hardware can't (fully) crop, downscale or rotate them.
/// </summary>
/// <remarks>
/// Call it from <see cref="AbstractScreenCapture{TColor}.PerformCaptureZoneUpdate"/> with the buffer passed to it.
/// A zone is cropped, downscaled and rotated in this order; the hardware may do the first steps, these methods do the rest.
/// </remarks>
public static class CaptureZoneWriter
{
    #region Methods

    /// <summary>
    /// Writes the pixels of a zone from a captured frame into its buffer: cropped, downscaled and rotated into the orientation of the display.
    /// </summary>
    /// <remarks>
    /// For backends that capture the whole frame into memory. If the hardware already cropped and downscaled, use <see cref="WriteFromScaled{TColor}"/>.
    /// <para>
    /// Downscaling averages blocks of 2^level x 2^level pixels from the top left of the region on the display. Partial blocks on the right and bottom are ignored;
    /// a region smaller than a block is averaged as one block.
    /// </para>
    /// </remarks>
    /// <param name="frame">The captured frame. Rotated by <paramref name="rotation"/> it covers the display.</param>
    /// <param name="captureZone">The zone to write.</param>
    /// <param name="buffer">The buffer passed to <see cref="AbstractScreenCapture{TColor}.PerformCaptureZoneUpdate"/>.</param>
    /// <param name="rotation">
    /// How <paramref name="frame"/> is rotated (clockwise) into the orientation of the display: <see cref="Display.Rotation"/> for a frame in the native orientation of the output,
    /// <see cref="ImageRotation.None"/> for one already in the orientation of the display.
    /// </param>
    /// <typeparam name="TColor">The color format of the zone.</typeparam>
    /// <exception cref="ArgumentNullException"><paramref name="captureZone"/> is <c>null</c>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The region of <paramref name="captureZone"/> doesn't lie within <paramref name="frame"/> (rotated by <paramref name="rotation"/>).</exception>
    /// <exception cref="ArgumentException"><paramref name="buffer"/> is too small for the zone.</exception>
    public static void Write<TColor>(RefImage<TColor> frame, CaptureZone<TColor> captureZone, Span<TColor> buffer, ImageRotation rotation = ImageRotation.None)
        where TColor : unmanaged, IColor<TColor>
    {
        ArgumentNullException.ThrowIfNull(captureZone);

        CaptureRegion region = captureZone.Region;
        (int frameWidth, int frameHeight) = PixelHelper.GetRotatedSize(frame.Width, frame.Height, rotation);
        if (!region.IsWithin(frameWidth, frameHeight))
            throw new ArgumentOutOfRangeException(nameof(captureZone), region, $"The region of the zone doesn't lie within the frame ({frameWidth}x{frameHeight} in the orientation of the display).");

        CaptureRegion source = GetSourceRegion(region, frameWidth, frameHeight, rotation);
        WriteRegion(frame[source.X, source.Y, source.Width, source.Height], region.Width, region.Height, captureZone, buffer, rotation);
    }

    /// <summary>
    /// Writes the pixels of a zone from its already cropped and downscaled region into its buffer, rotated into the orientation of the display.
    /// </summary>
    /// <remarks>
    /// For backends whose hardware crops and downscales (e.g. DX11).
    /// </remarks>
    /// <param name="scaled">The downscaled region of the zone. Rotated by <paramref name="rotation"/> it has the size of the zone.</param>
    /// <param name="captureZone">The zone to write.</param>
    /// <param name="buffer">The buffer passed to <see cref="AbstractScreenCapture{TColor}.PerformCaptureZoneUpdate"/>.</param>
    /// <param name="rotation">How <paramref name="scaled"/> is rotated (clockwise) into the orientation of the display (see <see cref="Write{TColor}"/>).</param>
    /// <typeparam name="TColor">The color format of the zone.</typeparam>
    /// <exception cref="ArgumentNullException"><paramref name="captureZone"/> is <c>null</c>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="scaled"/> (rotated by <paramref name="rotation"/>) doesn't have the size of the zone, or <paramref name="buffer"/> is too small for the zone.
    /// </exception>
    public static void WriteFromScaled<TColor>(RefImage<TColor> scaled, CaptureZone<TColor> captureZone, Span<TColor> buffer, ImageRotation rotation = ImageRotation.None)
        where TColor : unmanaged, IColor<TColor>
    {
        ArgumentNullException.ThrowIfNull(captureZone);

        (int width, int height) = PixelHelper.GetRotatedSize(scaled.Width, scaled.Height, rotation);
        if ((width != captureZone.Width) || (height != captureZone.Height))
            throw new ArgumentException($"The image has a size of {width}x{height} (in the orientation of the display), but the zone has {captureZone.Width}x{captureZone.Height}.", nameof(scaled));

        scaled.Rotate(rotation, buffer);
    }

    /// <summary>
    /// Gets the region of a frame in the native orientation of the output that is shown at <paramref name="region"/> on the rotated display.
    /// </summary>
    /// <param name="region">The region on the display.</param>
    /// <param name="display">The display.</param>
    /// <returns>The region in the coordinates of the frame.</returns>
    public static CaptureRegion GetSourceRegion(CaptureRegion region, Display display)
        => GetSourceRegion(region, display.Width, display.Height, display.Rotation);

    /// <summary>
    /// Gets the region of a frame that is shown at <paramref name="region"/> once the frame is rotated by <paramref name="rotation"/>.
    /// </summary>
    /// <param name="region">The region in the rotated frame.</param>
    /// <param name="width">The width of the rotated frame.</param>
    /// <param name="height">The height of the rotated frame.</param>
    /// <param name="rotation">How the frame is rotated (clockwise) into the orientation of <paramref name="region"/>.</param>
    /// <returns>The region in the coordinates of the frame before the rotation.</returns>
    public static CaptureRegion GetSourceRegion(CaptureRegion region, int width, int height, ImageRotation rotation)
        => rotation switch
        {
            ImageRotation.Rotate90 => new CaptureRegion(region.Y, width - region.X - region.Width, region.Height, region.Width),
            ImageRotation.Rotate180 => new CaptureRegion(width - region.X - region.Width, height - region.Y - region.Height, region.Width, region.Height),
            ImageRotation.Rotate270 => new CaptureRegion(height - region.Y - region.Height, region.X, region.Height, region.Width),
            _ => region
        };

    // width and height are the size of source after the rotation.
    private static void WriteRegion<TColor>(RefImage<TColor> source, int width, int height, CaptureZone<TColor> captureZone, Span<TColor> buffer, ImageRotation rotation)
        where TColor : unmanaged, IColor<TColor>
    {
        int factor = 1 << captureZone.DownscaleLevel;
        (int scaledWidth, int scaledHeight) = PixelHelper.GetDownscaledSize(width, height, factor);

        if (rotation == ImageRotation.None)
        {
            if (factor == 1)
                source.CopyTo(buffer);
            else
                source.Downscale(factor, buffer);
        }
        else if (factor == 1)
            source.Rotate(rotation, buffer);
        else
            DownscaleAndRotate(source, width, height, factor, scaledWidth, scaledHeight, buffer, rotation);
    }

    // Downscales first, so only the smaller image is rotated. The blocks are cut on the display, which is another side before the rotation.
    private static void DownscaleAndRotate<TColor>(RefImage<TColor> source, int width, int height, int factor, int scaledWidth, int scaledHeight,
                                                   Span<TColor> buffer, ImageRotation rotation)
        where TColor : unmanaged, IColor<TColor>
    {
        // Like HPPH: a region smaller than a block is one block.
        int blockWidth = Math.Min(factor, width);
        int blockHeight = Math.Min(factor, height);

        CaptureRegion blocks = GetSourceRegion(new CaptureRegion(0, 0, scaledWidth * blockWidth, scaledHeight * blockHeight), width, height, rotation);
        bool swapped = rotation is ImageRotation.Rotate90 or ImageRotation.Rotate270;
        (int factorX, int factorY) = swapped ? (blockHeight, blockWidth) : (blockWidth, blockHeight);

        (int unrotatedWidth, int unrotatedHeight) = PixelHelper.GetRotatedSize(scaledWidth, scaledHeight, rotation);
        int length = unrotatedWidth * unrotatedHeight;

        TColor[] scaled = ArrayPool<TColor>.Shared.Rent(length);
        try
        {
            Span<TColor> pixels = scaled.AsSpan(0, length);
            source[blocks.X, blocks.Y, blocks.Width, blocks.Height].Downscale(factorX, factorY, pixels);
            RefImage<TColor>.Wrap(pixels, unrotatedWidth, unrotatedHeight).Rotate(rotation, buffer);
        }
        finally
        {
            ArrayPool<TColor>.Shared.Return(scaled);
        }
    }

    #endregion
}
