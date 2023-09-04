namespace ScreenCapture.NET;

public interface IColor
{
    byte R { get; }
    byte G { get; }
    byte B { get; }
    byte A { get; }

#if NET7_0_OR_GREATER
    public static abstract ColorFormat ColorFormat { get; }
#else
    public static ColorFormat GetColorFormat<TColor>()
        where TColor : IColor
    {
        System.Type colorType = typeof(TColor);
        if (colorType == typeof(ColorBGRA)) return ColorFormat.BGRA;

        throw new System.ArgumentException($"Not ColorFormat registered for '{typeof(TColor).Name}'");
    }
#endif
}