using System;
using HPPH;

namespace ScreenCapture.NET;

/// <summary>
/// Represents the pixels of an <see cref="ICaptureZone"/> while it's locked, for code that doesn't know the color format at compile time.
/// </summary>
/// <remarks>
/// Get it from <see cref="ICaptureZone.Lock"/> and dispose it as soon as possible, preferably with <c>using</c>: while it's held, the capture writes the zone's second buffer, and a frame held over two captures makes the zone skip a capture.
/// <para>
/// Use <see cref="Execute{TOperation, TResult}(TOperation)"/> to process the pixels without knowing their color format,
/// or <see cref="AsRefImage{TColor}"/> if the format is known.
/// </para>
/// <para>
/// The pixels are only valid until the frame is disposed. Don't keep the spans or images obtained from it, use <see cref="CopyImage"/> to keep a copy.
/// </para>
/// <para>
/// The frame is a view of the zone at the time it was locked and keeps its size if the zone is changed later. Dispose it exactly once and on the thread that locked it, and don't copy it.
/// </para>
/// </remarks>
public ref struct CaptureFrame
{
    #region Properties & Fields

    private ZoneBuffer? _reader;
    private readonly ColorDispatcher _dispatcher;
    private readonly ReadOnlySpan<byte> _buffer;

    /// <summary>
    /// Gets the color format of the pixels.
    /// </summary>
    public readonly IColorFormat ColorFormat => _dispatcher.ColorFormat;

    /// <summary>
    /// Gets the width of the frame in pixels.
    /// </summary>
    public int Width { get; }

    /// <summary>
    /// Gets the height of the frame in pixels.
    /// </summary>
    public int Height { get; }

    /// <summary>
    /// Gets the distance between the starts of two rows in <see cref="RawBuffer"/>, in bytes.
    /// </summary>
    public int Stride { get; }

    /// <summary>
    /// Gets the bytes of the frame.
    /// </summary>
    public readonly ReadOnlySpan<byte> RawBuffer => _buffer;

    #endregion

    #region Constructors

    /// <summary>
    /// Creates a frame that holds a reader of <paramref name="buffer"/> (already added by the caller).
    /// </summary>
    internal CaptureFrame(ColorDispatcher dispatcher, ZoneBuffer buffer)
    {
        this._dispatcher = dispatcher;
        this._reader = buffer;
        this._buffer = buffer.RawData;
        this.Width = buffer.Width;
        this.Height = buffer.Height;
        this.Stride = buffer.Stride;
    }

    #endregion

    #region Methods

    /// <summary>
    /// Gets a <see cref="Image{TColor}"/> of the frame.
    /// </summary>
    /// <typeparam name="TColor">The color format of the zone.</typeparam>
    /// <returns>The <see cref="Image{TColor}"/> of the frame.</returns>
    /// <exception cref="ArgumentException"><typeparamref name="TColor"/> is not the color format of the zone.</exception>
    public readonly RefImage<TColor> AsRefImage<TColor>()
        where TColor : unmanaged, IColor<TColor>
    {
        if (_dispatcher is not ColorDispatcher<TColor>) throw new ArgumentException("The requested color format does not match the data.", nameof(TColor));

        return RefImage<TColor>.Wrap(_buffer, Width, Height, Stride);
    }

    /// <summary>
    /// Executes <paramref name="operation"/> on the pixels of the frame.
    /// </summary>
    /// <param name="operation">The operation to execute.</param>
    /// <typeparam name="TOperation">The type of the operation.</typeparam>
    /// <typeparam name="TResult">The type of the result of the operation.</typeparam>
    /// <returns>The result of the operation.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="operation"/> is <see langword="null"/>.</exception>
    public readonly TResult Execute<TOperation, TResult>(TOperation operation)
        where TOperation : IImageOperation<TResult>, allows ref struct
    {
        if (operation is null) throw new ArgumentNullException(nameof(operation));

        return _dispatcher.Execute<TOperation, TResult>(operation, _buffer, Width, Height, Stride);
    }

    /// <summary>
    /// Executes <paramref name="operation"/> on the pixels of the frame.
    /// </summary>
    /// <remarks>
    /// Struct operations are boxed. Use <see cref="Execute{TOperation, TResult}(TOperation)"/> to avoid that.
    /// </remarks>
    /// <param name="operation">The operation to execute.</param>
    /// <typeparam name="TResult">The type of the result of the operation.</typeparam>
    /// <returns>The result of the operation.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="operation"/> is <see langword="null"/>.</exception>
    public readonly TResult Execute<TResult>(IImageOperation<TResult> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);

        return _dispatcher.Execute<IImageOperation<TResult>, TResult>(operation, _buffer, Width, Height, Stride);
    }

    /// <summary>
    /// Copies the pixels of the frame into a new <see cref="IImage"/> that you own.
    /// </summary>
    /// <remarks>
    /// Use it to keep the pixels after the frame is disposed, or to pass them to other threads or async code.
    /// <see cref="RawBuffer"/> and the images from <see cref="AsRefImage{TColor}"/> are only valid while the frame is held.
    /// </remarks>
    /// <returns>The copy of the pixels.</returns>
    public readonly IImage CopyImage() => ColorFormat.CreateImage(_buffer, Width, Height, Stride);

    /// <summary>
    /// Releases the pixels, so the capture can write them again.
    /// </summary>
    public void Dispose()
    {
        ZoneBuffer? reader = _reader;
        _reader = null;
        reader?.RemoveReader();
    }

    #endregion
}
