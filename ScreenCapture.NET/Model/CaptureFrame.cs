using System;
using System.Runtime.InteropServices;
using HPPH;

namespace ScreenCapture.NET;

/// <summary>
/// Represents the pixels of a <see cref="CaptureZone{TColor}"/> while it's held.
/// </summary>
/// <remarks>
/// Get it from <see cref="CaptureZone{TColor}.AcquireFrame"/> and dispose it as soon as possible, preferably with <c>using</c>. A frame held too long makes the zone skip captures.
/// <para>
/// The pixels are only valid until the frame is disposed. Use <see cref="CopyImage"/> to keep them.
/// Dispose it exactly once, on the thread that acquired it, and don't copy it.
/// </para>
/// </remarks>
/// <typeparam name="TColor">The color format of the zone.</typeparam>
public ref struct CaptureFrame<TColor>
    where TColor : unmanaged, IColor<TColor>
{
    #region Properties & Fields

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
    /// Creates a frame that holds a reader of <paramref name="buffer"/> (already added by the caller).
    /// </summary>
    internal CaptureFrame(ZoneBuffer<TColor> buffer)
    {
        this._reader = buffer;
        this._pixels = buffer.Data;
        this.Width = buffer.Width;
        this.Height = buffer.Height;
        this.Stride = buffer.Stride;
    }

    #endregion

    #region Methods

    /// <summary>
    /// Copies the pixels of the frame into a new <see cref="Image{TColor}"/>, e.g. to keep them after the frame is disposed.
    /// </summary>
    /// <returns>The copy of the pixels.</returns>
    public readonly Image<TColor> CopyImage() => Image.ToImage();

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
