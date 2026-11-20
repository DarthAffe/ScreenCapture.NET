using System;
using System.Buffers;
using HPPH;

namespace ScreenCapture.NET;

/// <summary>
/// Writes the pixels of capture zones in software, for backends whose hardware can't (fully) crop, downscale or rotate them.
/// </summary>
/// <remarks>
/// Call the write methods from <see cref="AbstractScreenCapture{TColor}.PerformCaptureZoneUpdate"/>, with the buffer passed to it.
/// A zone goes through crop, downscale and rotation in this order: the hardware may do the first steps, these methods do the rest.
/// </remarks>
public static class CaptureZoneWriter
{
    #region Methods

    /// <summary>
    /// Writes the pixels of a zone from a captured frame into its buffer: the zone's region, downscaled by its level and rotated into the orientation of the display.
    /// </summary>
    /// <remarks>
    /// For backends that capture the whole frame into memory. This does all three steps; if the hardware already cropped and downscaled the region,
    /// use <see cref="WriteFromScaled{TColor}"/> instead.
    /// <para>
    /// Downscaling averages blocks of 2^level x 2^level pixels, starting at the top left of the region on the display;
    /// columns on the right and rows at the bottom (on the display) that don't fill a whole block are ignored. A region smaller than a block is averaged as one block.
    /// </para>
    /// </remarks>
    /// <param name="frame">The captured frame. Rotated by <paramref name="rotation"/> it covers the display: the region of the zone is in these coordinates.</param>
    /// <param name="captureZone">The zone to write.</param>
    /// <param name="buffer">The buffer passed to <see cref="AbstractScreenCapture{TColor}.PerformCaptureZoneUpdate"/>.</param>
    /// <param name="rotation">
    /// How <paramref name="frame"/> is rotated (clockwise) into the orientation of the display: <see cref="Display.Rotation"/> for a frame in the native orientation of the output,
    /// <see cref="ImageRotation.None"/> for a frame that already is in the orientation of the display.
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
    /// Writes the pixels of a zone from its already cropped and downscaled region into its buffer: rotated into the orientation of the display.
    /// </summary>
    /// <remarks>
    /// For backends whose hardware crops and downscales the region (e.g. DX11, from a frame in the native orientation of the output).
    /// </remarks>
    /// <param name="scaled">The downscaled region of the zone, rotated by <paramref name="rotation"/> it has the size of the zone.</param>
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
    /// <remarks>
    /// For backends whose hardware crops the zone's region out of a frame in the native orientation of the output
    /// (the size of the frame is the one of the display rotated back, see <see cref="PixelHelper.GetRotatedSize"/>).
    /// </remarks>
    /// <param name="region">The region on the display.</param>
    /// <param name="display">The display, its <see cref="Display.Rotation"/> is the one of the frame.</param>
    /// <returns>The region in the coordinates of the frame.</returns>
    public static CaptureRegion GetSourceRegion(CaptureRegion region, Display display)
        => GetSourceRegion(region, display.Width, display.Height, display.Rotation);

    /// <summary>
    /// Gets the region of a frame that is shown at <paramref name="region"/> once the frame is rotated by <paramref name="rotation"/>.
    /// </summary>
    /// <remarks>
    /// A pixel at (x, y) of the rotated frame is at (x, y) before a <see cref="ImageRotation.None"/>, at (y, width - 1 - x) before a <see cref="ImageRotation.Rotate90"/>,
    /// at (width - 1 - x, height - 1 - y) before a <see cref="ImageRotation.Rotate180"/> and at (height - 1 - y, x) before a <see cref="ImageRotation.Rotate270"/>.
    /// </remarks>
    /// <param name="region">The region in the rotated frame (e.g. on the display).</param>
    /// <param name="width">The width of the rotated frame (e.g. of the display).</param>
    /// <param name="height">The height of the rotated frame (e.g. of the display).</param>
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

    // source is the region of the zone before the rotation; width x height is its size after it (the size of the zone's region).
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

    // Downscales before rotating, so only the smaller image is rotated.
    // The blocks are the ones of the display: the region is cut to whole blocks there (dropping its right and bottom), which is another side before the rotation.
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
