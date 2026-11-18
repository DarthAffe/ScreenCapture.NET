using System;
using System.Runtime.InteropServices;
using System.Threading;
using HPPH;

namespace ScreenCapture.NET;

/// <summary>
/// Represents the pixels of a <see cref="CaptureZone{TColor}"/> while it's locked.
/// </summary>
/// <remarks>
/// Get it from <see cref="CaptureZone{TColor}.Lock"/> and dispose it as soon as possible, preferably with <c>using</c>: while it's held, the capture writes the zone's second buffer or, if the zone isn't double-buffered, waits for it.
/// <para>
/// The pixels are only valid until the frame is disposed. Don't keep the spans or images obtained from it, use <see cref="CopyImage"/> to keep a copy.
/// </para>
/// <para>
/// The frame is a view of the zone at the time it was locked and keeps its size if the zone is changed later. Dispose it exactly once and on the thread that locked it, and don't copy it.
/// </para>
/// </remarks>
/// <typeparam name="TColor">The color format of the zone.</typeparam>
public ref struct CaptureFrame<TColor>
    where TColor : unmanaged, IColor<TColor>
{
    #region Properties & Fields

    private Lock.Scope _scope;
    private ZoneBuffer? _reader;
    private readonly ReadOnlySpan<TColor> _pixels;

    /// <summary>
    /// Gets the color format of the pixels.
    /// </summary>
    public readonly IColorFormat ColorFormat => TColor.ColorFormat;

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
    public readonly ReadOnlySpan<byte> RawBuffer => MemoryMarshal.AsBytes(_pixels);

    /// <summary>
    /// Gets the pixels of the frame, row after row.
    /// </summary>
    public readonly ReadOnlySpan<TColor> Pixels => _pixels;

    /// <summary>
    /// Gets a <see cref="Image{TColor}"/> of the frame.
    /// </summary>
    public readonly RefImage<TColor> Image => RefImage<TColor>.Wrap(_pixels, Width, Height);

    #endregion

    #region Constructors

    /// <summary>
    /// Creates a frame of a single-buffered zone that holds the zone lock.
    /// </summary>
    internal CaptureFrame(ZoneBuffer<TColor> buffer, Lock.Scope scope)
        : this(buffer, scope, null)
    { }

    /// <summary>
    /// Creates a frame of a double-buffered zone that holds a reader of <paramref name="buffer"/> (already added by the caller).
    /// </summary>
    internal CaptureFrame(ZoneBuffer<TColor> buffer)
        : this(buffer, default, buffer)
    { }

    private CaptureFrame(ZoneBuffer<TColor> buffer, Lock.Scope scope, ZoneBuffer? reader)
    {
        this._scope = scope;
        this._reader = reader;
        this._pixels = buffer.Data;
        this.Width = buffer.Width;
        this.Height = buffer.Height;
        this.Stride = buffer.Stride;
    }

    #endregion

    #region Methods

    /// <summary>
    /// Copies the pixels of the frame into a new <see cref="Image{TColor}"/> that you own.
    /// </summary>
    /// <remarks>
    /// Use it to keep the pixels after the frame is disposed, or to pass them to other threads or async code.
    /// <see cref="Image"/> is only valid while the frame is held.
    /// </remarks>
    /// <returns>The copy of the pixels.</returns>
    public readonly Image<TColor> CopyImage() => Image.ToImage();

    /// <summary>
    /// Releases the pixels, so the capture can write them again.
    /// </summary>
    public void Dispose()
    {
        _scope.Dispose();

        ZoneBuffer? reader = _reader;
        _reader = null;
        reader?.RemoveReader();
    }

    #endregion
}
