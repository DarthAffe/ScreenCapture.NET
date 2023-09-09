// DarthAffe 07.09.2023: Copied from https://github.com/DarthAffe/RGB.NET/blob/2e0754f474b82ed4d0cae5c6c44378d234f1321b/RGB.NET.Core/Rendering/Textures/Sampler/SamplerInfo.cs

using System;

namespace ScreenCapture.NET.Downscale;

/// <summary>
/// Represents the information used to sample data.
/// </summary>
/// <typeparam name="T">The type of the data to sample.</typeparam>
internal readonly ref struct SamplerInfo<T>
{
    #region Properties & Fields

    private readonly ReadOnlySpan<T> _data;
    private readonly int _x;
    private readonly int _y;
    private readonly int _stride;
    private readonly int _dataPerPixel;
    private readonly int _dataWidth;

    /// <summary>
    /// Gets the width of the region the data comes from.
    /// </summary>
    public readonly int Width;

    /// <summary>
    /// Gets the height of region the data comes from.
    /// </summary>
    public readonly int Height;

    /// <summary>
    /// Gets the data for the requested row.
    /// </summary>
    /// <param name="row">The row to get the data for.</param>
    /// <returns>A readonly span containing the data of the row.</returns>
    public ReadOnlySpan<T> this[int row] => _data.Slice((((_y + row) * _stride) + _x) * _dataPerPixel, _dataWidth);

    #endregion

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="SamplerInfo{T}" /> class.
    /// </summary>
    /// <param name="width">The width of the region the data comes from.</param>
    /// <param name="height">The height of region the data comes from.</param>
    /// <param name="data">The data to sample.</param>
    public SamplerInfo(int x, int y, int width, int height, int stride, int dataPerPixel, in ReadOnlySpan<T> data)
    {
        this._x = x;
        this._y = y;
        this._data = data;
        this._stride = stride;
        this._dataPerPixel = dataPerPixel;
        this.Width = width;
        this.Height = height;

        _dataWidth = width * dataPerPixel;
    }

    #endregion
}