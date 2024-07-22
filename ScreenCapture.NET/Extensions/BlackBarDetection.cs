using HPPH;

namespace ScreenCapture.NET;

/// <summary>
/// Helper-class for black-bar removal.
/// </summary>
public static class BlackBarDetection
{
    #region IImage

    /// <summary>
    /// Create an image with black bars removed
    /// </summary>
    /// <param name="image">The image the bars are removed from.</param>
    /// <param name="threshold">The threshold of "blackness" used to detect black bars. (e. g. Threshold 5 will consider a pixel of color [5,5,5] as black.)</param>
    /// <param name="removeTop">A bool indicating if black bars should be removed at the top of the image.</param>
    /// <param name="removeBottom">A bool indicating if black bars should be removed at the bottom of the image.</param>
    /// <param name="removeLeft">A bool indicating if black bars should be removed on the left side of the image.</param>
    /// <param name="removeRight">A bool indicating if black bars should be removed on the right side of the image.</param>
    /// <returns>The image with black bars removed.</returns>
    public static IImage RemoveBlackBars(this IImage image, int threshold = 0, bool removeTop = true, bool removeBottom = true, bool removeLeft = true, bool removeRight = true)
    {
        int top = removeTop ? CalculateTop(image, threshold) : 0;
        int bottom = removeBottom ? CalculateBottom(image, threshold) : image.Height;
        int left = removeLeft ? CalculateLeft(image, threshold) : 0;
        int right = removeRight ? CalculateRight(image, threshold) : image.Width;

        return image[left, top, right - left, bottom - top];
    }

    /// <summary>
    /// Calculates the first row starting from the top with at least one non black pixel.
    /// </summary>
    /// <param name="image">The image to check.</param>
    /// <param name="threshold">The threshold of "blackness" used to detect black bars. (e. g. Threshold 5 will consider a pixel of color [5,5,5] as black.)</param>
    /// <returns>The row number of the first row with at least one non-black pixel.</returns>
    public static int CalculateTop(IImage image, int threshold)
    {
        IImageRows rows = image.Rows;
        for (int y = 0; y < rows.Count; y++)
        {
            IImageRow row = rows[y];
            foreach (IColor color in row)
            {
                if ((color.R > threshold) || (color.G > threshold) || (color.B > threshold))
                    return y;
            }
        }

        return 0;
    }

    /// <summary>
    /// Calculates the last row starting from the top with at least one non black pixel.
    /// </summary>
    /// <param name="image">The image to check.</param>
    /// <param name="threshold">The threshold of "blackness" used to detect black bars. (e. g. Threshold 5 will consider a pixel of color [5,5,5] as black.)</param>
    /// <returns>The row number of the last row with at least one non-black pixel.</returns>
    public static int CalculateBottom(IImage image, int threshold)
    {
        IImageRows rows = image.Rows;
        for (int y = rows.Count - 1; y >= 0; y--)
        {
            IImageRow row = rows[y];
            foreach (IColor color in row)
            {
                if ((color.R > threshold) || (color.G > threshold) || (color.B > threshold))
                    return y;
            }
        }

        return rows.Count;
    }

    /// <summary>
    /// Calculates the first column starting from the left with at least one non black pixel.
    /// </summary>
    /// <param name="image">The image to check.</param>
    /// <param name="threshold">The threshold of "blackness" used to detect black bars. (e. g. Threshold 5 will consider a pixel of color [5,5,5] as black.)</param>
    /// <returns>The column number of the first column with at least one non-black pixel.</returns>
    public static int CalculateLeft(IImage image, int threshold)
    {
        IImageColumns columns = image.Columns;
        for (int x = 0; x < columns.Count; x++)
        {
            IImageColumn column = columns[x];
            foreach (IColor color in column)
            {
                if ((color.R > threshold) || (color.G > threshold) || (color.B > threshold))
                    return x;
            }
        }

        return 0;
    }

    /// <summary>
    /// Calculates the last column starting from the top with at least one non black pixel.
    /// </summary>
    /// <param name="image">The image to check.</param>
    /// <param name="threshold">The threshold of "blackness" used to detect black bars. (e. g. Threshold 5 will consider a pixel of color [5,5,5] as black.)</param>
    /// <returns>The column number of the last column with at least one non-black pixel.</returns>
    public static int CalculateRight(IImage image, int threshold)
    {
        IImageColumns columns = image.Columns;
        for (int x = columns.Count - 1; x >= 0; x--)
        {
            IImageColumn column = columns[x];
            foreach (IColor color in column)
            {
                if ((color.R > threshold) || (color.G > threshold) || (color.B > threshold))
                    return x;
            }
        }

        return columns.Count;
    }

    #endregion


    #region Image

    /// <summary>
    /// Create an image with black bars removed
    /// </summary>
    /// <param name="image">The image the bars are removed from.</param>
    /// <param name="threshold">The threshold of "blackness" used to detect black bars. (e. g. Threshold 5 will consider a pixel of color [5,5,5] as black.)</param>
    /// <param name="removeTop">A bool indicating if black bars should be removed at the top of the image.</param>
    /// <param name="removeBottom">A bool indicating if black bars should be removed at the bottom of the image.</param>
    /// <param name="removeLeft">A bool indicating if black bars should be removed on the left side of the image.</param>
    /// <param name="removeRight">A bool indicating if black bars should be removed on the right side of the image.</param>
    /// <returns>The image with black bars removed.</returns>
    public static RefImage<T> RemoveBlackBars<T>(this IImage<T> image, int threshold = 0, bool removeTop = true, bool removeBottom = true, bool removeLeft = true, bool removeRight = true)
        where T : struct, IColor
    {
        int top = removeTop ? CalculateTop(image, threshold) : 0;
        int bottom = removeBottom ? CalculateBottom(image, threshold) : image.Height;
        int left = removeLeft ? CalculateLeft(image, threshold) : 0;
        int right = removeRight ? CalculateRight(image, threshold) : image.Width;

        return image[left, top, right - left, bottom - top];
    }

    /// <summary>
    /// Calculates the first row starting from the top with at least one non black pixel.
    /// </summary>
    /// <param name="image">The image to check.</param>
    /// <param name="threshold">The threshold of "blackness" used to detect black bars. (e. g. Threshold 5 will consider a pixel of color [5,5,5] as black.)</param>
    /// <returns>The row number of the first row with at least one non-black pixel.</returns>
    public static int CalculateTop<T>(IImage<T> image, int threshold)
        where T : struct, IColor
    {
        ImageRows<T> rows = image.Rows;
        for (int y = 0; y < rows.Count; y++)
        {
            ImageRow<T> row = rows[y];
            foreach (T color in row)
            {
                if ((color.R > threshold) || (color.G > threshold) || (color.B > threshold))
                    return y;
            }
        }

        return 0;
    }

    /// <summary>
    /// Calculates the last row starting from the top with at least one non black pixel.
    /// </summary>
    /// <param name="image">The image to check.</param>
    /// <param name="threshold">The threshold of "blackness" used to detect black bars. (e. g. Threshold 5 will consider a pixel of color [5,5,5] as black.)</param>
    /// <returns>The row number of the last row with at least one non-black pixel.</returns>
    public static int CalculateBottom<T>(IImage<T> image, int threshold)
        where T : struct, IColor
    {
        ImageRows<T> rows = image.Rows;
        for (int y = rows.Count - 1; y >= 0; y--)
        {
            ImageRow<T> row = rows[y];
            foreach (T color in row)
            {
                if ((color.R > threshold) || (color.G > threshold) || (color.B > threshold))
                    return y;
            }
        }

        return rows.Count;
    }

    /// <summary>
    /// Calculates the first column starting from the left with at least one non black pixel.
    /// </summary>
    /// <param name="image">The image to check.</param>
    /// <param name="threshold">The threshold of "blackness" used to detect black bars. (e. g. Threshold 5 will consider a pixel of color [5,5,5] as black.)</param>
    /// <returns>The column number of the first column with at least one non-black pixel.</returns>
    public static int CalculateLeft<T>(IImage<T> image, int threshold)
        where T : struct, IColor
    {
        ImageColumns<T> columns = image.Columns;
        for (int x = 0; x < columns.Count; x++)
        {
            ImageColumn<T> column = columns[x];
            foreach (T color in column)
            {
                if ((color.R > threshold) || (color.G > threshold) || (color.B > threshold))
                    return x;
            }
        }

        return 0;
    }

    /// <summary>
    /// Calculates the last column starting from the top with at least one non black pixel.
    /// </summary>
    /// <param name="image">The image to check.</param>
    /// <param name="threshold">The threshold of "blackness" used to detect black bars. (e. g. Threshold 5 will consider a pixel of color [5,5,5] as black.)</param>
    /// <returns>The column number of the last column with at least one non-black pixel.</returns>
    public static int CalculateRight<T>(IImage<T> image, int threshold)
        where T : struct, IColor
    {
        ImageColumns<T> columns = image.Columns;
        for (int x = columns.Count - 1; x >= 0; x--)
        {
            ImageColumn<T> column = columns[x];
            foreach (T color in column)
            {
                if ((color.R > threshold) || (color.G > threshold) || (color.B > threshold))
                    return x;
            }
        }

        return columns.Count;
    }

    #endregion

    #region RefImage

    /// <summary>
    /// Create an image with black bars removed
    /// </summary>
    /// <param name="image">The image the bars are removed from.</param>
    /// <param name="threshold">The threshold of "blackness" used to detect black bars. (e. g. Threshold 5 will consider a pixel of color [5,5,5] as black.)</param>
    /// <param name="removeTop">A bool indicating if black bars should be removed at the top of the image.</param>
    /// <param name="removeBottom">A bool indicating if black bars should be removed at the bottom of the image.</param>
    /// <param name="removeLeft">A bool indicating if black bars should be removed on the left side of the image.</param>
    /// <param name="removeRight">A bool indicating if black bars should be removed on the right side of the image.</param>
    /// <returns>The image with black bars removed.</returns>
    public static RefImage<TColor> RemoveBlackBars<TColor>(this RefImage<TColor> image, int threshold = 0, bool removeTop = true, bool removeBottom = true, bool removeLeft = true, bool removeRight = true)
        where TColor : struct, IColor
    {
        int top = removeTop ? CalculateTop(image, threshold) : 0;
        int bottom = removeBottom ? CalculateBottom(image, threshold) : image.Height - 1;
        int left = removeLeft ? CalculateLeft(image, threshold) : 0;
        int right = removeRight ? CalculateRight(image, threshold) : image.Width - 1;

        return image[left, top, (right - left) + 1, (bottom - top) + 1];
    }

    /// <summary>
    /// Calculates the first row starting from the top with at least one non black pixel.
    /// </summary>
    /// <param name="image">The image to check.</param>
    /// <param name="threshold">The threshold of "blackness" used to detect black bars. (e. g. Threshold 5 will consider a pixel of color [5,5,5] as black.)</param>
    /// <returns>The row number of the first row with at least one non-black pixel.</returns>
    public static int CalculateTop<TColor>(this RefImage<TColor> image, int threshold)
        where TColor : struct, IColor
    {
        ImageRows<TColor> rows = image.Rows;
        for (int y = 0; y < rows.Count; y++)
        {
            ImageRow<TColor> row = rows[y];
            foreach (TColor color in row)
            {
                if ((color.R > threshold) || (color.G > threshold) || (color.B > threshold))
                    return y;
            }
        }

        return 0;
    }

    /// <summary>
    /// Calculates the last row starting from the top with at least one non black pixel.
    /// </summary>
    /// <param name="image">The image to check.</param>
    /// <param name="threshold">The threshold of "blackness" used to detect black bars. (e. g. Threshold 5 will consider a pixel of color [5,5,5] as black.)</param>
    /// <returns>The row number of the last row with at least one non-black pixel.</returns>
    public static int CalculateBottom<TColor>(this RefImage<TColor> image, int threshold)
        where TColor : struct, IColor
    {
        ImageRows<TColor> rows = image.Rows;
        for (int y = rows.Count - 1; y >= 0; y--)
        {
            ImageRow<TColor> row = rows[y];
            foreach (TColor color in row)
            {
                if ((color.R > threshold) || (color.G > threshold) || (color.B > threshold))
                    return y;
            }
        }

        return rows.Count;
    }

    /// <summary>
    /// Calculates the first column starting from the left with at least one non black pixel.
    /// </summary>
    /// <param name="image">The image to check.</param>
    /// <param name="threshold">The threshold of "blackness" used to detect black bars. (e. g. Threshold 5 will consider a pixel of color [5,5,5] as black.)</param>
    /// <returns>The column number of the first column with at least one non-black pixel.</returns>
    public static int CalculateLeft<TColor>(this RefImage<TColor> image, int threshold)
        where TColor : struct, IColor
    {
        ImageColumns<TColor> columns = image.Columns;
        for (int x = 0; x < columns.Count; x++)
        {
            ImageColumn<TColor> column = columns[x];
            foreach (TColor color in column)
            {
                if ((color.R > threshold) || (color.G > threshold) || (color.B > threshold))
                    return x;
            }
        }

        return 0;
    }

    /// <summary>
    /// Calculates the last column starting from the top with at least one non black pixel.
    /// </summary>
    /// <param name="image">The image to check.</param>
    /// <param name="threshold">The threshold of "blackness" used to detect black bars. (e. g. Threshold 5 will consider a pixel of color [5,5,5] as black.)</param>
    /// <returns>The column number of the last column with at least one non-black pixel.</returns>
    public static int CalculateRight<TColor>(this RefImage<TColor> image, int threshold)
        where TColor : struct, IColor
    {
        ImageColumns<TColor> columns = image.Columns;
        for (int x = columns.Count - 1; x >= 0; x--)
        {
            ImageColumn<TColor> column = columns[x];
            foreach (TColor color in column)
            {
                if ((color.R > threshold) || (color.G > threshold) || (color.B > threshold))
                    return x;
            }
        }

        return columns.Count;
    }

    #endregion
}