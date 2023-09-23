// ReSharper disable MemberCanBePrivate.Global

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace ScreenCapture.NET;

/// <summary>
/// Represents a duplicated region on the screen.
/// </summary>
public sealed class CaptureZone<TColor> : ICaptureZone
    where TColor : struct, IColor
{
    #region Properties & Fields

    private readonly object _lock = new();

    /// <inheritdoc />
    public Display Display { get; }

#if NET7_0_OR_GREATER
    /// <inheritdoc />
    public ColorFormat ColorFormat
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => TColor.ColorFormat;
    }
#else
    /// <inheritdoc />
    public ColorFormat ColorFormat
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => default(TColor).Net6ColorFormat;
    }
#endif

    /// <inheritdoc />
    public int X { get; internal set; }

    /// <inheritdoc />
    public int Y { get; internal set; }

    /// <inheritdoc />
    public int Width { get; private set; }

    /// <inheritdoc />
    public int Height { get; private set; }

    /// <inheritdoc />
    public int Stride
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Width * ColorFormat.BytesPerPixel;
    }

    /// <inheritdoc />
    public int DownscaleLevel { get; private set; }

    /// <inheritdoc />
    public int UnscaledWidth { get; private set; }

    /// <inheritdoc />
    public int UnscaledHeight { get; private set; }

    internal byte[] InternalBuffer { get; set; }

    /// <inheritdoc />
    public ReadOnlySpan<byte> RawBuffer
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => InternalBuffer;
    }

    /// <summary>
    /// Gets the pixel-data of this zone.
    /// </summary>
    public ReadOnlySpan<TColor> Pixels
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => MemoryMarshal.Cast<byte, TColor>(RawBuffer);
    }

    /// <summary>
    /// Gets a <see cref="RefImage{TColor}"/>. Basically the same as <see cref="Image"/> but with better performance.
    /// </summary>
    public RefImage<TColor> Image
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => new(Pixels, 0, 0, Width, Height, Width);
    }

    /// <inheritdoc />
    IImage ICaptureZone.Image
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => new Image<TColor>(InternalBuffer, 0, 0, Width, Height, Width);
    }

    /// <inheritdoc />
    public bool AutoUpdate { get; set; } = true;

    /// <inheritdoc />
    public bool IsUpdateRequested { get; private set; }

#endregion

    #region Events

    /// <inheritdoc />
    public event EventHandler? Updated;

    #endregion

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="CaptureZone{T}"/> class.
    /// </summary>
    /// <param name="id">The unique id of this <see cref="CaptureZone{T}"/>.</param>
    /// <param name="x">The x-location of the region on the screen.</param>
    /// <param name="y">The y-location of the region on the screen.</param>
    /// <param name="width">The width of the region on the screen.</param>
    /// <param name="height">The height of the region on the screen.</param>
    /// <param name="bytesPerPixel">The number of bytes per pixel.</param>
    /// <param name="downscaleLevel">The level of downscaling applied to the image of this region before copying to local memory.</param>
    /// <param name="unscaledWidth">The original width of the region.</param>
    /// <param name="unscaledHeight">The original height of the region</param>
    /// <param name="buffer">The buffer containing the image data.</param>
    internal CaptureZone(Display display, int x, int y, int width, int height, int downscaleLevel, int unscaledWidth, int unscaledHeight)
    {
        this.Display = display;
        this.X = x;
        this.Y = y;
        this.Width = width;
        this.Height = height;
        this.DownscaleLevel = downscaleLevel;
        this.UnscaledWidth = unscaledWidth;
        this.UnscaledHeight = unscaledHeight;

        InternalBuffer = new byte[Stride * Height];
    }

    #endregion

    #region Methods

    /// <inheritdoc />
    public RefImage<T> GetRefImage<T>()
        where T : struct, IColor
    {
        if (typeof(T) != typeof(TColor)) throw new ArgumentException("The requested Color-Format does not match the data.", nameof(T));

        return new RefImage<T>(MemoryMarshal.Cast<byte, T>(RawBuffer), 0, 0, Width, Height, Width);
    }

    /// <inheritdoc />
    public IDisposable Lock()
    {
        Monitor.Enter(_lock);
        return new UnlockDisposable(_lock);
    }

    /// <inheritdoc />
    public void RequestUpdate() => IsUpdateRequested = true;

    /// <summary>
    /// Marks the <see cref="CaptureZone{T}"/> as updated.
    /// </summary>
    internal void SetUpdated()
    {
        IsUpdateRequested = false;

        Updated?.Invoke(this, EventArgs.Empty);
    }

    internal void Resize(int width, int height, int downscaleLevel, int unscaledWidth, int unscaledHeight)
    {
        Width = width;
        Height = height;
        DownscaleLevel = downscaleLevel;
        UnscaledWidth = unscaledWidth;
        UnscaledHeight = unscaledHeight;

        int newBufferSize = Stride * Height;
        if (newBufferSize != InternalBuffer.Length)
            InternalBuffer = new byte[newBufferSize];
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
        ~UnlockDisposable() => Dispose();

        #endregion

        #region Methods

        /// <inheritdoc />
        public void Dispose()
        {
            if (_disposed) throw new ObjectDisposedException("The lock is already released");

            Monitor.Exit(_lock);
            _disposed = true;

            GC.SuppressFinalize(this);
        }

        #endregion
    }
}