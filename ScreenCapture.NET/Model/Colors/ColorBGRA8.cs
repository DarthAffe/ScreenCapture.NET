using System.Runtime.InteropServices;

namespace ScreenCapture.NET;

[StructLayout(LayoutKind.Sequential)]
public readonly struct ColorBGRA8 : IColor
{
    #region Properties & Fields

    private readonly byte _r;
    private readonly byte _g;
    private readonly byte _b;
    private readonly byte _a;

    public byte A => _a;
    public byte R => _r;
    public byte G => _g;
    public byte B => _b;

    public float sA => _a.GetPercentageFromByteValue();
    public float sR => _r.GetPercentageFromByteValue();
    public float sG => _g.GetPercentageFromByteValue();
    public float sB => _b.GetPercentageFromByteValue();

    #endregion

    #region Constructors

    public ColorBGRA8()
    { }

    public ColorBGRA8(byte r, byte g, byte b, byte a)
    {
        this._r = r;
        this._g = g;
        this._b = b;
        this._a = a;
    }

    #endregion
}