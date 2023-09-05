// ReSharper disable ConvertToAutoProperty

using System.Runtime.InteropServices;

namespace ScreenCapture.NET;

[StructLayout(LayoutKind.Sequential)]
public readonly struct ColorBGRA : IColor
{
    #region Properties & Fields

    public static ColorFormat ColorFormat => ColorFormat.BGRA;

    private readonly byte _b;
    private readonly byte _g;
    private readonly byte _r;
    private readonly byte _a;

    public byte B => _b;
    public byte G => _g;
    public byte R => _r;
    public byte A => _a;

    #endregion

    #region Constructors

    public ColorBGRA(byte b, byte g, byte r, byte a)
    {
        this._b = b;
        this._g = g;
        this._r = r;
        this._a = a;
    }

    #endregion
}