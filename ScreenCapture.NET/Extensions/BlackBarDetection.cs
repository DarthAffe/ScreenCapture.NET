using System;
using HPPH;

namespace ScreenCapture.NET;

/// <summary>
/// Helper-class for black-bar removal.
/// </summary>
/// <remarks>
/// A pixel counts as black if its red, green and blue components are all less than or equal to the threshold.
/// A completely black image has no content to crop to and is returned uncropped.
/// </remarks>
public static class BlackBarDetection
{
    #region Constants

    // Rows of a column strip checked at once. Strips with content are usually decided by the first block.
    private const int ROW_BLOCK_HEIGHT = 32;
    
    #endregion

    #region Properties & Fields
    
    // Columns are searched in strips, coarse to fine down to single columns. The finer widths match the vectorized MinMax paths (64 and 16 bytes).
    private static ReadOnlySpan<int> StripWidths => [256, 64, 16, 4, 1];
    
    #endregion

    #region Methods

    /// <summary>
    /// Calculates the first row starting from the top with at least one non black pixel.
    /// </summary>
    /// <param name="image">The image to check.</param>
    /// <param name="threshold">The threshold of "blackness" used to detect black bars. (e. g. Threshold 5 will consider a pixel of color [5,5,5] as black.)</param>
    /// <returns>The index of the first row with at least one non-black pixel, or 0 if the image is completely black.</returns>
    public static int CalculateTop(this IImage image, int threshold)
    {
        ArgumentNullException.ThrowIfNull(image);
        return image.Execute<EdgeOperation, int>(new EdgeOperation(Edge.Top, threshold));
    }

    /// <inheritdoc cref="CalculateTop(IImage, int)"/>
    public static int CalculateTop<TColor>(this Image<TColor> image, int threshold)
        where TColor : unmanaged, IColor<TColor>
    {
        ArgumentNullException.ThrowIfNull(image);
        return image.AsRefImage().CalculateTop(threshold);
    }

    /// <inheritdoc cref="CalculateTop(IImage, int)"/>
    public static int CalculateTop<TColor>(this RefImage<TColor> image, int threshold)
        where TColor : unmanaged, IColor<TColor>
    {
        int row = FirstContentRow(image, threshold);
        return row < 0 ? 0 : row;
    }

    /// <summary>
    /// Calculates the end of the content at the bottom: the row after the last row with at least one non black pixel.
    /// </summary>
    /// <param name="image">The image to check.</param>
    /// <param name="threshold">The threshold of "blackness" used to detect black bars. (e. g. Threshold 5 will consider a pixel of color [5,5,5] as black.)</param>
    /// <returns>The index of the last row with at least one non-black pixel plus one (exclusive), or the height of the image if it is completely black.</returns>
    public static int CalculateBottom(this IImage image, int threshold)
    {
        ArgumentNullException.ThrowIfNull(image);
        return image.Execute<EdgeOperation, int>(new EdgeOperation(Edge.Bottom, threshold));
    }

    /// <inheritdoc cref="CalculateBottom(IImage, int)"/>
    public static int CalculateBottom<TColor>(this Image<TColor> image, int threshold)
        where TColor : unmanaged, IColor<TColor>
    {
        ArgumentNullException.ThrowIfNull(image);
        return image.AsRefImage().CalculateBottom(threshold);
    }

    /// <inheritdoc cref="CalculateBottom(IImage, int)"/>
    public static int CalculateBottom<TColor>(this RefImage<TColor> image, int threshold)
        where TColor : unmanaged, IColor<TColor>
    {
        int row = LastContentRow(image, threshold);
        return row < 0 ? image.Height : row + 1;
    }

    /// <summary>
    /// Calculates the first column starting from the left with at least one non black pixel.
    /// </summary>
    /// <param name="image">The image to check.</param>
    /// <param name="threshold">The threshold of "blackness" used to detect black bars. (e. g. Threshold 5 will consider a pixel of color [5,5,5] as black.)</param>
    /// <returns>The index of the first column with at least one non-black pixel, or 0 if the image is completely black.</returns>
    public static int CalculateLeft(this IImage image, int threshold)
    {
        ArgumentNullException.ThrowIfNull(image);
        return image.Execute<EdgeOperation, int>(new EdgeOperation(Edge.Left, threshold));
    }

    /// <inheritdoc cref="CalculateLeft(IImage, int)"/>
    public static int CalculateLeft<TColor>(this Image<TColor> image, int threshold)
        where TColor : unmanaged, IColor<TColor>
    {
        ArgumentNullException.ThrowIfNull(image);
        return image.AsRefImage().CalculateLeft(threshold);
    }

    /// <inheritdoc cref="CalculateLeft(IImage, int)"/>
    public static int CalculateLeft<TColor>(this RefImage<TColor> image, int threshold)
        where TColor : unmanaged, IColor<TColor>
    {
        int column = FirstContentColumn(image, threshold);
        return column < 0 ? 0 : column;
    }

    /// <summary>
    /// Calculates the end of the content on the right: the column after the last column with at least one non black pixel.
    /// </summary>
    /// <param name="image">The image to check.</param>
    /// <param name="threshold">The threshold of "blackness" used to detect black bars. (e. g. Threshold 5 will consider a pixel of color [5,5,5] as black.)</param>
    /// <returns>The index of the last column with at least one non-black pixel plus one (exclusive), or the width of the image if it is completely black.</returns>
    public static int CalculateRight(this IImage image, int threshold)
    {
        ArgumentNullException.ThrowIfNull(image);
        return image.Execute<EdgeOperation, int>(new EdgeOperation(Edge.Right, threshold));
    }

    /// <inheritdoc cref="CalculateRight(IImage, int)"/>
    public static int CalculateRight<TColor>(this Image<TColor> image, int threshold)
        where TColor : unmanaged, IColor<TColor>
    {
        ArgumentNullException.ThrowIfNull(image);
        return image.AsRefImage().CalculateRight(threshold);
    }

    /// <inheritdoc cref="CalculateRight(IImage, int)"/>
    public static int CalculateRight<TColor>(this RefImage<TColor> image, int threshold)
        where TColor : unmanaged, IColor<TColor>
    {
        int column = LastContentColumn(image, threshold);
        return column < 0 ? image.Width : column + 1;
    }

    /// <summary>
    /// Create an image with black bars removed.
    /// </summary>
    /// <param name="image">The image the bars are removed from.</param>
    /// <param name="threshold">The threshold of "blackness" used to detect black bars. (e. g. Threshold 5 will consider a pixel of color [5,5,5] as black.)</param>
    /// <param name="removeTop">A bool indicating if black bars should be removed at the top of the image.</param>
    /// <param name="removeBottom">A bool indicating if black bars should be removed at the bottom of the image.</param>
    /// <param name="removeLeft">A bool indicating if black bars should be removed on the left side of the image.</param>
    /// <param name="removeRight">A bool indicating if black bars should be removed on the right side of the image.</param>
    /// <returns>The image with black bars removed. The whole image if it is completely black.</returns>
    public static IImage RemoveBlackBars(this IImage image, int threshold = 0, bool removeTop = true, bool removeBottom = true, bool removeLeft = true, bool removeRight = true)
    {
        ArgumentNullException.ThrowIfNull(image);

        Bounds bounds = image.Execute<BoundsOperation, Bounds>(new BoundsOperation(threshold, removeTop, removeBottom, removeLeft, removeRight));
        return image.Slice(bounds.Left, bounds.Top, bounds.Width, bounds.Height);
    }

    /// <summary>
    /// Create an image with black bars removed.
    /// </summary>
    /// <param name="image">The image the bars are removed from.</param>
    /// <param name="threshold">The threshold of "blackness" used to detect black bars. (e. g. Threshold 5 will consider a pixel of color [5,5,5] as black.)</param>
    /// <param name="removeTop">A bool indicating if black bars should be removed at the top of the image.</param>
    /// <param name="removeBottom">A bool indicating if black bars should be removed at the bottom of the image.</param>
    /// <param name="removeLeft">A bool indicating if black bars should be removed on the left side of the image.</param>
    /// <param name="removeRight">A bool indicating if black bars should be removed on the right side of the image.</param>
    /// <returns>The image with black bars removed, sharing the buffer of <paramref name="image"/>. The whole image if it is completely black.</returns>
    public static Image<T> RemoveBlackBars<T>(this Image<T> image, int threshold = 0, bool removeTop = true, bool removeBottom = true, bool removeLeft = true, bool removeRight = true)
        where T : unmanaged, IColor<T>
    {
        ArgumentNullException.ThrowIfNull(image);

        Bounds bounds = CalculateBounds(image.AsRefImage(), threshold, removeTop, removeBottom, removeLeft, removeRight);
        return image.Slice(bounds.Left, bounds.Top, bounds.Width, bounds.Height);
    }

    /// <summary>
    /// Create an image with black bars removed.
    /// </summary>
    /// <param name="image">The image the bars are removed from.</param>
    /// <param name="threshold">The threshold of "blackness" used to detect black bars. (e. g. Threshold 5 will consider a pixel of color [5,5,5] as black.)</param>
    /// <param name="removeTop">A bool indicating if black bars should be removed at the top of the image.</param>
    /// <param name="removeBottom">A bool indicating if black bars should be removed at the bottom of the image.</param>
    /// <param name="removeLeft">A bool indicating if black bars should be removed on the left side of the image.</param>
    /// <param name="removeRight">A bool indicating if black bars should be removed on the right side of the image.</param>
    /// <returns>The image with black bars removed. The whole image if it is completely black.</returns>
    public static RefImage<TColor> RemoveBlackBars<TColor>(this RefImage<TColor> image, int threshold = 0, bool removeTop = true, bool removeBottom = true, bool removeLeft = true, bool removeRight = true)
        where TColor : unmanaged, IColor<TColor>
    {
        Bounds bounds = CalculateBounds(image, threshold, removeTop, removeBottom, removeLeft, removeRight);
        return image.Slice(bounds.Left, bounds.Top, bounds.Width, bounds.Height);
    }

    private static Bounds CalculateBounds<T>(RefImage<T> image, int threshold, bool removeTop, bool removeBottom, bool removeLeft, bool removeRight)
        where T : unmanaged, IColor<T>
    {
        Bounds whole = new(0, 0, image.Width, image.Height);
        if (!(removeTop || removeBottom || removeLeft || removeRight)) return whole;

        int top = FirstContentRow(image, threshold);
        if (top < 0) return whole; // completely black

        int bottom = LastContentRow(image, threshold) + 1;

        // Rows outside are black, so only the content rows are checked for columns.
        RefImage<T> content = image[0, top, image.Width, bottom - top];
        int left = removeLeft ? FirstContentColumn(content, threshold) : 0;
        int right = removeRight ? LastContentColumn(content, threshold) + 1 : image.Width;

        return new Bounds(left, removeTop ? top : 0, right, removeBottom ? bottom : image.Height);
    }

    /// <returns>The index of the first row with content, or -1 if there is none.</returns>
    private static int FirstContentRow<T>(RefImage<T> image, int threshold)
        where T : unmanaged, IColor<T>
    {
        if (IsEmpty(image)) return -1;

        ImageRows<T> rows = image.Rows;
        for (int y = 0; y < rows.Count; y++)
            if (HasContent(rows[y].AsSpan().MinMax(), threshold))
                return y;

        return -1;
    }

    /// <returns>The index of the last row with content, or -1 if there is none.</returns>
    private static int LastContentRow<T>(RefImage<T> image, int threshold)
        where T : unmanaged, IColor<T>
    {
        if (IsEmpty(image)) return -1;

        ImageRows<T> rows = image.Rows;
        for (int y = rows.Count - 1; y >= 0; y--)
            if (HasContent(rows[y].AsSpan().MinMax(), threshold))
                return y;

        return -1;
    }

    /// <returns>The index of the first column with content, or -1 if there is none.</returns>
    private static int FirstContentColumn<T>(RefImage<T> image, int threshold)
        where T : unmanaged, IColor<T>
    {
        if (IsEmpty(image)) return -1;

        int height = image.Height;
        int start = 0;
        int end = image.Width;
        foreach (int stripWidth in StripWidths)
        {
            bool found = false;
            for (int stripStart = start; stripStart < end; stripStart += stripWidth)
            {
                int stripEnd = Math.Min(stripStart + stripWidth, end);
                if (HasContent(image[stripStart, 0, stripEnd - stripStart, height], threshold))
                {
                    (start, end) = (stripStart, stripEnd);
                    found = true;
                    break;
                }
            }

            // Only possible at the first width.
            if (!found) return -1;
        }

        return start;
    }

    /// <returns>The index of the last column with content, or -1 if there is none.</returns>
    private static int LastContentColumn<T>(RefImage<T> image, int threshold)
        where T : unmanaged, IColor<T>
    {
        if (IsEmpty(image)) return -1;

        int height = image.Height;
        int start = 0;
        int end = image.Width;
        foreach (int stripWidth in StripWidths)
        {
            bool found = false;
            for (int stripEnd = end; stripEnd > start; stripEnd -= stripWidth)
            {
                int stripStart = Math.Max(stripEnd - stripWidth, start);
                if (HasContent(image[stripStart, 0, stripEnd - stripStart, height], threshold))
                {
                    (start, end) = (stripStart, stripEnd);
                    found = true;
                    break;
                }
            }

            // Only possible at the first width.
            if (!found) return -1;
        }

        return start;
    }

    private static bool IsEmpty<T>(RefImage<T> image)
        where T : unmanaged, IColor<T>
        => (image.Width == 0) || (image.Height == 0);

    // Block by block, so strips with content are decided early.
    private static bool HasContent<T>(RefImage<T> strip, int threshold)
        where T : unmanaged, IColor<T>
    {
        int width = strip.Width;
        int height = strip.Height;
        for (int y = 0; y < height; y += ROW_BLOCK_HEIGHT)
            if (HasContent(strip[0, y, width, Math.Min(ROW_BLOCK_HEIGHT, height - y)].MinMax(), threshold))
                return true;

        return false;
    }

    private static bool HasContent(ColorMinMax minMax, int threshold)
        => (minMax.RedMax > threshold) || (minMax.GreenMax > threshold) || (minMax.BlueMax > threshold);

    #endregion

    #region Operations

    private enum Edge { Top, Bottom, Left, Right }

    private readonly record struct Bounds(int Left, int Top, int Right, int Bottom)
    {
        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }

    private readonly struct EdgeOperation(Edge edge, int threshold) : IImageOperation<int>
    {
        public int Execute<T>(RefImage<T> image)
            where T : unmanaged, IColor<T>
            => edge switch
            {
                Edge.Top => image.CalculateTop(threshold),
                Edge.Bottom => image.CalculateBottom(threshold),
                Edge.Left => image.CalculateLeft(threshold),
                _ => image.CalculateRight(threshold)
            };
    }

    private readonly struct BoundsOperation(int threshold, bool removeTop, bool removeBottom, bool removeLeft, bool removeRight) : IImageOperation<Bounds>
    {
        public Bounds Execute<T>(RefImage<T> image)
            where T : unmanaged, IColor<T>
            => CalculateBounds(image, threshold, removeTop, removeBottom, removeLeft, removeRight);
    }

    #endregion
}
