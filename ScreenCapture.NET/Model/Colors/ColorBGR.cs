// ReSharper disable ConvertToAutoProperty

using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ScreenCapture.NET;

[DebuggerDisplay("[A: {A}, R: {R}, G: {G}, B: {B}]")]
[StructLayout(LayoutKind.Sequential)]
public readonly struct ColorBGR : IColor
{
    #region Properties & Fields

    public static ColorFormat ColorFormat => ColorFormat.BGR;

    private readonly byte _b;
    private readonly byte _g;
    private readonly byte _r;

    // ReSharper disable ConvertToAutoPropertyWhenPossible
    public byte A => byte.MaxValue;
    public byte B => _b;
    public byte G => _g;
    public byte R => _r;
    // ReSharper restore ConvertToAutoPropertyWhenPossible

    #endregion

    #region Constructors

    public ColorBGR(byte b, byte g, byte r)
    {
        this._b = b;
        this._g = g;
        this._r = r;
    }

    #endregion

    #region Methods

    public override string ToString() => $"[A: {A}, R: {_r}, G: {_g}, B: {_b}]";

    #endregion
}