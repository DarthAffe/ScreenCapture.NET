using System.Collections.Generic;

namespace ScreenCapture.NET;

/// <summary>
/// Represents a image.
/// </summary>
public interface IImage : IEnumerable<IColor>
{
    /// <summary>
    /// Gets the width of this image.
    /// </summary>
    int Width { get; }

    /// <summary>
    /// Gets the height of this image.
    /// </summary>
    int Height { get; }

    /// <summary>
    /// Gets the color at the specified location.
    /// </summary>
    /// <param name="x">The X-location to read.</param>
    /// <param name="y">The Y-location to read.</param>
    /// <returns>The color at the specified location.</returns>
    IColor this[int x, int y] { get; }

    /// <summary>
    /// Gets an image representing the specified location.
    /// </summary>
    /// <param name="x">The X-location of the image.</param>
    /// <param name="y">The Y-location of the image.</param>
    /// <param name="width">The width of the sub-image.</param>
    /// <param name="height"></param>
    /// <returns></returns>
    IImage this[int x, int y, int width, int height] { get; }

    /// <summary>
    /// Gets a list of all rows of this image.
    /// </summary>
    IImageRows Rows { get; }

    /// <summary>
    /// Gets a list of all columns of this image.
    /// </summary>
    IImageColumns Columns { get; }

    /// <summary>
    /// Represents a list of rows of an image.
    /// </summary>
    public interface IImageRows : IEnumerable<IImageRow>
    {
        /// <summary>
        /// Gets the amount of rows in this list.
        /// </summary>
        int Count { get; }

        /// <summary>
        /// Gets a specific <see cref="IImageRow"/>.
        /// </summary>
        /// <param name="column">The ´row to get.</param>
        /// <returns>The requested <see cref="IImageRow"/>.</returns>
        IImageRow this[int column] { get; }
    }

    /// <summary>
    /// Represents a list of columns of an image.
    /// </summary>
    public interface IImageColumns : IEnumerable<IImageColumn>
    {
        /// <summary>
        /// Gets the amount of columns in this list.
        /// </summary>
        int Count { get; }

        /// <summary>
        /// Gets a specific <see cref="IImageColumn"/>.
        /// </summary>
        /// <param name="column">The column to get.</param>
        /// <returns>The requested <see cref="IImageColumn"/>.</returns>
        IImageColumn this[int column] { get; }
    }

    /// <summary>
    /// Represents a single row of an image.
    /// </summary>
    public interface IImageRow : IEnumerable<IColor>
    {
        /// <summary>
        /// Gets the length of the row.
        /// </summary>
        int Length { get; }

        /// <summary>
        /// Gets the <see cref="IColor"/> at the specified location.
        /// </summary>
        /// <param name="x">The location to get the color from.</param>
        /// <returns>The <see cref="IColor"/> at the specified location.</returns>
        IColor this[int x] { get; }
    }

    /// <summary>
    /// Represents a single column of an image.
    /// </summary>
    public interface IImageColumn : IEnumerable<IColor>
    {
        /// <summary>
        /// Gets the length of the column.
        /// </summary>
        int Length { get; }

        /// <summary>
        /// Gets the <see cref="IColor"/> at the specified location.
        /// </summary>
        /// <param name="y">The location to get the color from.</param>
        /// <returns>The <see cref="IColor"/> at the specified location.</returns>
        IColor this[int y] { get; }
    }
}