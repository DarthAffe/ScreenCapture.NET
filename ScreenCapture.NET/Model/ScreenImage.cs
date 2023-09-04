using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace ScreenCapture.NET;

public sealed class ScreenImage<TColor> : IScreenImage
    where TColor : struct, IColor
{
    #region Properties & Fields

    private readonly object _lock = new();
    private byte[] _buffer;

    public Span<byte> Raw => _buffer;
    public Span<TColor> Pixels => MemoryMarshal.Cast<byte, TColor>(_buffer);

    public int Width { get; private set; }
    public int Height { get; private set; }
    public int Stride => Width * ColorFormat.BytesPerPixel;
    public ColorFormat ColorFormat { get; }

    #endregion

    #region Indexer

    IColor IScreenImage.this[int x, int y] => this[x, y];
    public TColor this[int x, int y] => Pixels[(y * Width) + x];

    #endregion

    #region Constructors

    internal ScreenImage(int width, int height, ColorFormat colorFormat)
    {
        this.Width = width;
        this.Height = height;
        this.ColorFormat = colorFormat;

        _buffer = new byte[width * height * colorFormat.BytesPerPixel];
    }

    #endregion

    #region Methods

    internal void Resize(int width, int height)
    {
        Width = width;
        Height = height;

        _buffer = new byte[width * height * ColorFormat.BytesPerPixel];
    }

    public IDisposable Lock()
    {
        Monitor.Enter(_lock);
        return new UnlockDisposable(_lock);
    }

    #endregion

    private class UnlockDisposable : IDisposable
    {
        #region Properties & Fields

        private bool _disposed = false;
        private readonly object _lock;

        #endregion

        #region Constructors

        public UnlockDisposable(object @lock) => this._lock = @lock;

        #endregion

        #region Methods

        public void Dispose()
        {
            if (_disposed) throw new ObjectDisposedException("The lock is already released");

            Monitor.Exit(_lock);
            _disposed = true;
        }

        #endregion
    }

    public readonly ref struct ScreemImageRow
    {
        private readonly Span<TColor> _pixels;

        public IColor this[int x] => _pixels[x];

        public ScreemImageRow(Span<TColor> pixels)
        {
            this._pixels = pixels;
        }
    }
}