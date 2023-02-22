using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using CommunityToolkit.HighPerformance;

namespace ScreenCapture.NET;

public abstract class PixelBuffer
{
    #region Properties & Fields

    public byte[] Raw { get; }

    public abstract ReadOnlySpan2D<IColor> Pixels { get; }

    #endregion

    #region Constructors

    protected PixelBuffer(byte[] buffer)
    {
        this.Raw = buffer;
    }

    #endregion
}

public sealed class PixelBuffer<TColor> : PixelBuffer
    where TColor : unmanaged, IColor
{
    #region Properties & Fields

    private readonly int _width;
    private readonly int _height;

    public override unsafe ReadOnlySpan2D<IColor> Pixels
    {
        get
        {
            TColor @ref = MemoryMarshal.AsRef<TColor>(Raw);
            return new ReadOnlySpan2D<IColor>(Unsafe.AsPointer(ref @ref), _height, _width, 0);
        }
    }

    #endregion

    #region Constructors

    public PixelBuffer(int width, int height)
        : base(new byte[width * height * Marshal.SizeOf<TColor>()])
    {
        this._width = width;
        this._height = height;
    }

    #endregion
}