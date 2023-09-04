﻿using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ScreenCapture.NET;

public readonly ref struct Image<TColor>
    where TColor : struct, IColor
{
    #region Properties & Fields

    private readonly ReadOnlySpan<TColor> _pixels;

    private readonly int _x;
    private readonly int _y;
    private readonly int _stride;

    public int Width { get; }
    public int Height { get; }

    #endregion

    #region Indexer

    public TColor this[int x, int y]
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            if ((x < 0) || (y < 0) || (x > Width) || (y > Height)) throw new IndexOutOfRangeException();

            return _pixels[((_y + y) * _stride) + (_x + x)];
        }
    }

    public Image<TColor> this[int x, int y, int width, int height]
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            if ((x < 0) || (y < 0) || ((x + width) > Width) || ((y + height) > Height)) throw new IndexOutOfRangeException();

            return new Image<TColor>(_pixels, _x + x, _y + y, width, height, _stride);
        }
    }

    public ImageRows Rows => new(_pixels, _x, _y, Width, Height, _stride);
    public ImageColumns Columns => new(_pixels, _x, _y, Width, Height, _stride);

    #endregion

    #region Constructors

    internal Image(ReadOnlySpan<TColor> pixels, int x, int y, int width, int height, int stride)
    {
        this._pixels = pixels;
        this._x = x;
        this._y = y;
        this.Width = width;
        this.Height = height;
        this._stride = stride;
    }

    #endregion

    #region Methods

    public void CopyTo(in Span<TColor> dest)
    {
        if (dest == null) throw new ArgumentNullException(nameof(dest));
        if (dest.Length < (Width * Height)) throw new ArgumentException("The destination is too small to fit this image.", nameof(dest));

        ImageRows rows = Rows;
        Span<TColor> target = dest;
        foreach (ReadOnlyRefEnumerable<TColor> row in rows)
        {
            row.CopyTo(target);
            target = target[Width..];
        }
    }

    #endregion

    #region Indexer-Structs

    public readonly ref struct ImageRows
    {
        #region Properties & Fields

        private readonly ReadOnlySpan<TColor> _pixels;
        private readonly int _x;
        private readonly int _y;
        private readonly int _width;
        private readonly int _height;
        private readonly int _stride;

        #endregion

        #region Indexer

        public readonly ReadOnlyRefEnumerable<TColor> this[int row]
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                if ((row < 0) || (row > _height)) throw new IndexOutOfRangeException();

                ref TColor r0 = ref MemoryMarshal.GetReference(_pixels);
                ref TColor rr = ref Unsafe.Add(ref r0, (nint)(uint)(((row + _y) * _stride) + _x));

                return new ReadOnlyRefEnumerable<TColor>(rr, _width, 1);
            }
        }

        #endregion

        #region Constructors

        public ImageRows(ReadOnlySpan<TColor> pixels, int x, int y, int width, int height, int stride)
        {
            this._pixels = pixels;
            this._x = x;
            this._y = y;
            this._width = width;
            this._height = height;
            this._stride = stride;
        }

        #endregion

        #region Methods

        /// <inheritdoc cref="System.Collections.IEnumerable.GetEnumerator"/>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Enumerator GetEnumerator() => new(this);

        #endregion

        public ref struct Enumerator
        {
            #region Properties & Fields

            private readonly ImageRows _rows;
            private int _position;

            /// <inheritdoc cref="System.Collections.Generic.IEnumerator{T}.Current"/>
            public ReadOnlyRefEnumerable<TColor> Current
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get => _rows[_position];
            }

            #endregion

            #region Constructors


            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal Enumerator(ImageRows rows)
            {
                this._rows = rows;

                _position = -1;
            }

            #endregion

            #region Methods

            /// <inheritdoc cref="System.Collections.IEnumerator.MoveNext"/>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public bool MoveNext() => ++_position < _rows._height;

            #endregion
        }
    }

    public readonly ref struct ImageColumns
    {
        #region Properties & Fields

        private readonly ReadOnlySpan<TColor> _pixels;
        private readonly int _x;
        private readonly int _y;
        private readonly int _width;
        private readonly int _height;
        private readonly int _stride;

        #endregion

        #region Indexer

        public ReadOnlyRefEnumerable<TColor> this[int column]
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                if ((column < 0) || (column > _width)) throw new IndexOutOfRangeException();

                ref TColor r0 = ref MemoryMarshal.GetReference(_pixels);
                ref TColor rc = ref Unsafe.Add(ref r0, (nint)(uint)((_y * _stride) + (column + _x)));

                return new ReadOnlyRefEnumerable<TColor>(rc, _height, _stride);
            }
        }

        #endregion

        #region Constructors

        public ImageColumns(ReadOnlySpan<TColor> pixels, int x, int y, int width, int height, int stride)
        {
            this._pixels = pixels;
            this._x = x;
            this._y = y;
            this._width = width;
            this._height = height;
            this._stride = stride;
        }

        #endregion

        #region Methods

        /// <inheritdoc cref="System.Collections.IEnumerable.GetEnumerator"/>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Enumerator GetEnumerator() => new(this);

        #endregion

        public ref struct Enumerator
        {
            #region Properties & Fields

            private readonly ImageColumns _columns;
            private int _position;

            /// <inheritdoc cref="System.Collections.Generic.IEnumerator{T}.Current"/>
            public ReadOnlyRefEnumerable<TColor> Current
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get => _columns[_position];
            }

            #endregion

            #region Constructors

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal Enumerator(ImageColumns columns)
            {
                this._columns = columns;
                this._position = -1;
            }

            #endregion

            #region Methods

            /// <inheritdoc cref="System.Collections.IEnumerator.MoveNext"/>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public bool MoveNext() => ++_position < _columns._width;

            #endregion
        }
    }

    #endregion
}