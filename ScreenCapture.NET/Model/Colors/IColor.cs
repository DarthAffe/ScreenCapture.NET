using System;

namespace ScreenCapture.NET;

public interface IColor
{
    byte R { get; }
    byte G { get; }
    byte B { get; }
    byte A { get; }

    public static virtual ColorFormat ColorFormat => throw new NotSupportedException();
}