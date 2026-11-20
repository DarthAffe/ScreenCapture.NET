using System;
using System.Runtime.InteropServices;
using System.Threading;
using HPPH;

namespace ScreenCapture.NET;

/// <summary>
/// A pixel buffer of a capture zone together with the size it was created for.
/// </summary>
/// <remarks>
/// Zones count the frames reading a buffer, so the capture only writes buffers nobody reads.
/// The rows have no padding.
/// </remarks>
internal abstract class ZoneBuffer
{
    #region Properties & Fields

    private int _readers;

    public int Width { get; }

    public int Height { get; }

    /// <summary>
    /// Gets the length of a row in bytes.
    /// </summary>
    public int Stride { get; }

    /// <summary>
    /// Gets the pixels as bytes, for code that doesn't know the color format.
    /// </summary>
    public abstract ReadOnlySpan<byte> RawData { get; }

    /// <summary>
    /// Gets if a frame reads this buffer.
    /// </summary>
    public bool IsInUse => Volatile.Read(ref _readers) > 0;

    #endregion

    #region Constructors

    protected ZoneBuffer(int width, int height, int stride)
    {
        this.Width = width;
        this.Height = height;
        this.Stride = stride;
    }

    #endregion

    #region Methods

    public bool Fits(int width, int height) => (Width == width) && (Height == height);

    public void AddReader() => Interlocked.Increment(ref _readers);

    public void RemoveReader() => Interlocked.Decrement(ref _readers);

    #endregion
}

/// <inheritdoc />
/// <typeparam name="TColor">The color format of the zone.</typeparam>
internal sealed class ZoneBuffer<TColor> : ZoneBuffer
    where TColor : unmanaged, IColor<TColor>
{
    #region Properties & Fields

    public TColor[] Data { get; }

    /// <inheritdoc />
    public override ReadOnlySpan<byte> RawData => MemoryMarshal.AsBytes(Data.AsSpan());

    #endregion

    #region Constructors

    public ZoneBuffer(int width, int height)
        : base(width, height, width * TColor.ColorFormat.BytesPerPixel)
    {
        Data = new TColor[width * height];
    }

    #endregion
}
