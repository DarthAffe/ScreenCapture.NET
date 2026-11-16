using System.Threading;

namespace ScreenCapture.NET;

/// <summary>
/// A pixel buffer of a capture zone together with the size it was created for.
/// </summary>
/// <remarks>
/// Double-buffered zones count the frames reading a buffer, so the capture only writes buffers nobody reads.
/// </remarks>
internal sealed class ZoneBuffer
{
    #region Properties & Fields

    private int _readers;

    public byte[] Data { get; }

    public int Width { get; }

    public int Height { get; }

    public int Stride { get; }

    /// <summary>
    /// Gets if a frame reads this buffer.
    /// </summary>
    public bool IsInUse => Volatile.Read(ref _readers) > 0;

    #endregion

    #region Constructors

    public ZoneBuffer(int width, int height, int stride)
    {
        this.Width = width;
        this.Height = height;
        this.Stride = stride;

        Data = new byte[stride * height];
    }

    #endregion

    #region Methods

    public bool Fits(int width, int height, int stride) => (Width == width) && (Height == height) && (Stride == stride);

    public void AddReader() => Interlocked.Increment(ref _readers);

    public void RemoveReader() => Interlocked.Decrement(ref _readers);

    #endregion
}
