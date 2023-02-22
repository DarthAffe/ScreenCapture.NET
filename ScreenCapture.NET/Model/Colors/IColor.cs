namespace ScreenCapture.NET;

public interface IColor
{
    byte A { get; }
    byte R { get; }
    byte G { get; }
    byte B { get; }

    float sA { get; }
    float sR { get; }
    float sG { get; }
    float sB { get; }
}
