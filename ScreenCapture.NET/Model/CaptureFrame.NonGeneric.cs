using System;
using HPPH;

namespace ScreenCapture.NET;

/// <summary>
/// Represents the pixels of an <see cref="ICaptureZone"/> while it's held, for code that doesn't know the color format at compile time.
/// </summary>
/// <remarks>
/// Get it from <see cref="ICaptureZone.AcquireFrame"/> and dispose it as soon as possible, preferably with <c>using</c>. A frame held too long makes the zone skip captures.
/// <para>
/// Use <see cref="Execute{TOperation, TResult}(TOperation)"/> to process the pixels without knowing their color format, or <see cref="AsRefImage{TColor}"/> if it's known.
/// </para>
/// <para>
/// The pixels are only valid until the frame is disposed. Use <see cref="CopyImage"/> to keep them.
/// Dispose it exactly once, on the thread that acquired it, and don't copy it.
/// </para>
/// </remarks>
public ref struct CaptureFrame
{
    #region Properties & Fields

    // Kept after Dispose cleared _reader.
    private readonly ZoneBuffer _zoneBuffer;
    private ZoneBuffer? _reader;
    private readonly ReadOnlySpan<byte> _buffer;

    /// <summary>
    /// Gets the color format of the pixels.
    /// </summary>
    public readonly IColorFormat ColorFormat => _zoneBuffer.ColorFormat;

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
    internal CaptureFrame(ZoneBuffer buffer)
    {
        this._zoneBuffer = buffer;
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
        if (_zoneBuffer is not ZoneBuffer<TColor>) throw new ArgumentException("The requested color format does not match the data.", nameof(TColor));

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

        return _zoneBuffer.Execute<TOperation, TResult>(operation);
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

        return _zoneBuffer.Execute<IImageOperation<TResult>, TResult>(operation);
    }

    /// <summary>
    /// Copies the pixels of the frame into a new <see cref="IImage"/>, e.g. to keep them after the frame is disposed.
    /// </summary>
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
