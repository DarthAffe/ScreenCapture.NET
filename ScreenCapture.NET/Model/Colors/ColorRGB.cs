// ReSharper disable ConvertToAutoProperty

using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ScreenCapture.NET;

[DebuggerDisplay("[A: {A}, R: {R}, G: {G}, B: {B}]")]
[StructLayout(LayoutKind.Sequential)]
public readonly struct ColorRGB : IColor
{
    #region Properties & Fields

    public static ColorFormat ColorFormat => ColorFormat.RGB;

    private readonly byte _r;
    private readonly byte _g;
    private readonly byte _b;

    // ReSharper disable ConvertToAutoPropertyWhenPossible
    public byte A => byte.MaxValue;
    public byte R => _r;
    public byte G => _g;
    public byte B => _b;
    // ReSharper restore ConvertToAutoPropertyWhenPossible

    #endregion

    #region Constructors

    public ColorRGB(byte r, byte g, byte b)
    {
        this._r = r;
        this._g = g;
        this._b = b;
    }

    #endregion

    #region Methods

    public override string ToString() => $"[A: {A}, R: {_r}, G: {_g}, B: {_b}]";

    #endregion
}