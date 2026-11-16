using System;
using System.Runtime.InteropServices;
using System.Threading;
using HPPH;

namespace ScreenCapture.NET;

/// <summary>
/// Represents the pixels of a <see cref="CaptureZone{TColor}"/> while its lock is held.
/// </summary>
/// <remarks>
/// Get it from <see cref="CaptureZone{TColor}.Lock"/> and dispose it as soon as possible, preferably with <c>using</c>: a capture waits for the lock to update the zone.
/// <para>
/// The pixels are only valid until the frame is disposed. Don't keep the spans or images obtained from it, use <see cref="CopyImage"/> to keep a copy.
/// </para>
/// <para>
/// The frame is a view of the zone at the time it was locked. Dispose it exactly once and on the thread that locked it, and don't copy it.
/// </para>
/// </remarks>
/// <typeparam name="TColor">The color format of the zone.</typeparam>
public ref struct CaptureFrame<TColor>
    where TColor : unmanaged, IColor<TColor>
{
    #region Properties & Fields

    private Lock.Scope _scope;
    private readonly ReadOnlySpan<byte> _buffer;

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
    public readonly ReadOnlySpan<byte> RawBuffer => _buffer;

    /// <summary>
    /// Gets the pixels of the frame, row after row.
    /// </summary>
    public readonly ReadOnlySpan<TColor> Pixels => MemoryMarshal.Cast<byte, TColor>(_buffer);

    /// <summary>
    /// Gets a <see cref="Image{TColor}"/> of the frame.
    /// </summary>
    public readonly RefImage<TColor> Image => RefImage<TColor>.Wrap(_buffer, Width, Height, Stride);

    #endregion

    #region Constructors

    internal CaptureFrame(Lock.Scope scope, ReadOnlySpan<byte> buffer, int width, int height, int stride)
    {
        this._scope = scope;
        this._buffer = buffer;
        this.Width = width;
        this.Height = height;
        this.Stride = stride;
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
    /// Releases the lock of the zone.
    /// </summary>
    public void Dispose() => _scope.Dispose();

    #endregion
}
