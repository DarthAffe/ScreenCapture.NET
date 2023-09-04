using System;

namespace ScreenCapture.NET;

public interface IScreenImage
{
    Span<byte> Raw { get; }
    int Width { get; }
    int Height { get; }
    int Stride { get; }
    ColorFormat ColorFormat { get; }

    IDisposable Lock();

    IColor this[int x, int y] { get; }
}
