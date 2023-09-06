using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ScreenCapture.NET;

public sealed class Image<TColor> : IImage
    where TColor : struct, IColor
{
    #region Properties & Fields

    private readonly byte[] _buffer;

    private readonly int _x;
    private readonly int _y;
    private readonly int _stride;

    public int Width { get; }
    public int Height { get; }

    #endregion

    #region Indexer

    public IColor this[int x, int y]
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            if ((x < 0) || (y < 0) || (x >= Width) || (y >= Height)) throw new IndexOutOfRangeException();

            return MemoryMarshal.Cast<byte, TColor>(_buffer)[((_y + y) * _stride) + (_x + x)];
        }
    }

    public IImage this[int x, int y, int width, int height]
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            if ((x < 0) || (y < 0) || (width <= 0) || (height <= 0) || ((x + width) > Width) || ((y + height) > Height)) throw new IndexOutOfRangeException();

            return new Image<TColor>(_buffer, _x + x, _y + y, width, height, _stride);
        }
    }

    public IImage.IImageRows Rows
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => new ImageRows(_buffer, _x, _y, Width, Height, _stride);
    }

    public IImage.IImageColumns Columns
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => new ImageColumns(_buffer, _x, _y, Width, Height, _stride);
    }

    #endregion

    #region Constructors

    internal Image(byte[] buffer, int x, int y, int width, int height, int stride)
    {
        this._buffer = buffer;
        this._x = x;
        this._y = y;
        this.Width = width;
        this.Height = height;
        this._stride = stride;
    }

    #endregion

    #region Methods

    public IEnumerator<IColor> GetEnumerator()
    {
        for (int y = 0; y < Height; y++)
            for (int x = 0; x < Width; x++)
                yield return this[x, y];
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    #endregion

    #region Indexer-Classes

    private sealed class ImageRows : IImage.IImageRows
    {
        #region Properties & Fields

        private readonly byte[] _buffer;
        private readonly int _x;
        private readonly int _y;
        private readonly int _width;
        private readonly int _height;
        private readonly int _stride;

        public int Count => _height;

        #endregion

        #region Indexer

        public IImage.IImageRow this[int row]
        {
            get
            {
                if ((row < 0) || (row >= _height)) throw new IndexOutOfRangeException();

                return new ImageRow(_buffer, (((row + _y) * _stride) + _x), _width);
            }
        }

        #endregion

        #region Constructors

        internal ImageRows(byte[] buffer, int x, int y, int width, int height, int stride)
        {
            this._buffer = buffer;
            this._x = x;
            this._y = y;
            this._width = width;
            this._height = height;
            this._stride = stride;
        }

        #endregion

        #region Methods

        public IEnumerator<IImage.IImageRow> GetEnumerator()
        {
            for (int y = 0; y < _height; y++)
                yield return this[y];
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        #endregion
    }

    private sealed class ImageRow : IImage.IImageRow
    {
        #region Properties & Fields

        private readonly byte[] _buffer;
        private readonly int _start;
        private readonly int _length;

        public int Length => _length;

        #endregion

        #region Indexer

        public IColor this[int x]
        {
            get
            {
                if ((x < 0) || (x >= _length)) throw new IndexOutOfRangeException();

                ReadOnlySpan<TColor> row = MemoryMarshal.Cast<byte, TColor>(_buffer)[_start..];
                return row[x];
            }
        }

        #endregion

        #region Constructors

        internal ImageRow(byte[] buffer, int start, int length)
        {
            this._buffer = buffer;
            this._start = start;
            this._length = length;
        }

        #endregion

        #region Methods

        public IEnumerator<IColor> GetEnumerator()
        {
            for (int x = 0; x < _length; x++)
                yield return this[x];
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        #endregion
    }

    private sealed class ImageColumns : IImage.IImageColumns
    {
        #region Properties & Fields

        private readonly byte[] _buffer;
        private readonly int _x;
        private readonly int _y;
        private readonly int _width;
        private readonly int _height;
        private readonly int _stride;

        public int Count => _width;

        #endregion

        #region Indexer

        public IImage.IImageColumn this[int column]
        {
            get
            {
                if ((column < 0) || (column >= _width)) throw new IndexOutOfRangeException();

                return new ImageColumn(_buffer, (_y * _stride) + _x + column, _height, _stride);
            }
        }

        #endregion

        #region Constructors

        internal ImageColumns(byte[] buffer, int x, int y, int width, int height, int stride)
        {
            this._buffer = buffer;
            this._x = x;
            this._y = y;
            this._width = width;
            this._height = height;
            this._stride = stride;
        }

        #endregion

        #region Methods

        public IEnumerator<IImage.IImageColumn> GetEnumerator()
        {
            for (int y = 0; y < _height; y++)
                yield return this[y];
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        #endregion
    }

    private sealed class ImageColumn : IImage.IImageColumn
    {
        #region Properties & Fields

        private readonly byte[] _buffer;
        private readonly int _start;
        private readonly int _length;
        private readonly int _step;

        public int Length => _length;

        #endregion

        #region Indexer

        public IColor this[int y]
        {
            get
            {
                if ((y < 0) || (y >= _length)) throw new IndexOutOfRangeException();

                ReadOnlySpan<TColor> row = MemoryMarshal.Cast<byte, TColor>(_buffer)[_start..];
                return row[y * _step];
            }
        }

        #endregion

        #region Constructors

        internal ImageColumn(byte[] buffer, int start, int length, int step)
        {
            this._buffer = buffer;
            this._start = start;
            this._length = length;
            this._step = step;
        }

        #endregion

        #region Methods

        public IEnumerator<IColor> GetEnumerator()
        {
            for (int y = 0; y < _length; y++)
                yield return this[y];
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        #endregion
    }

    #endregion
}