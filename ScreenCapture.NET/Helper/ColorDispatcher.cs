using System;
using HPPH;

namespace ScreenCapture.NET;

/// <summary>
/// Gives code that only knows the <see cref="IColorFormat"/> of a buffer typed access to it.
/// </summary>
internal abstract class ColorDispatcher
{
    #region Properties & Fields

    public abstract IColorFormat ColorFormat { get; }

    #endregion

    #region Methods

    public abstract TResult Execute<TOperation, TResult>(TOperation operation, ReadOnlySpan<byte> buffer, int width, int height, int stride)
        where TOperation : IImageOperation<TResult>, allows ref struct;

    #endregion
}

/// <inheritdoc />
internal sealed class ColorDispatcher<TColor> : ColorDispatcher
    where TColor : unmanaged, IColor<TColor>
{
    #region Properties & Fields

    public static ColorDispatcher<TColor> Instance { get; } = new();

    public override IColorFormat ColorFormat => TColor.ColorFormat;

    #endregion

    #region Constructors

    private ColorDispatcher() { }

    #endregion

    #region Methods

    public override TResult Execute<TOperation, TResult>(TOperation operation, ReadOnlySpan<byte> buffer, int width, int height, int stride)
        => operation.Execute(RefImage<TColor>.Wrap(buffer, width, height, stride));

    #endregion
}
