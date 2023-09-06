using System.Collections.Generic;

namespace ScreenCapture.NET;

public interface IImage : IEnumerable<IColor>
{
    int Width { get; }
    int Height { get; }

    IColor this[int x, int y] { get; }
    IImage this[int x, int y, int width, int height] { get; }

    IImageRows Rows { get; }
    IImageColumns Columns { get; }

    public interface IImageRows : IEnumerable<IImageRow>
    {
        int Count { get; }
        IImageRow this[int column] { get; }
    }

    public interface IImageColumns : IEnumerable<IImageColumn>
    {
        int Count { get; }
        IImageColumn this[int column] { get; }
    }

    public interface IImageRow : IEnumerable<IColor>
    {
        int Length { get; }
        IColor this[int x] { get; }
    }

    public interface IImageColumn : IEnumerable<IColor>
    {
        int Length { get; }
        IColor this[int y] { get; }
    }
}