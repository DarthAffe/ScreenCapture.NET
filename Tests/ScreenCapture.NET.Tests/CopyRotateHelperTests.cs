using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using HPPH;

namespace ScreenCapture.NET.Tests;

/// <summary>
/// Pins down the current output of the DX11 rotation copy (zone 3x2).
/// The direction itself is confirmed on hardware in step 3.4; these tests make sure refactorings keep it.
/// </summary>
[TestClass]
[OSCondition(OperatingSystems.Windows)]
[SupportedOSPlatform("windows")]
public sealed class CopyRotateHelperTests
{
    #region Constants

    private const int WIDTH = 3;
    private const int HEIGHT = 2;
    private const byte PADDING = 0xEE;

    #endregion

    #region Methods

    /// <summary>
    /// Source pixel at row <paramref name="row"/>, column <paramref name="column"/>.
    /// </summary>
    private static ColorBGRA S(int row, int column) => new((byte)column, (byte)row, 0x42, 0xFF);

    /// <summary>
    /// Creates a source texture with <paramref name="rows"/> rows of <paramref name="columns"/> pixels and 8 padding bytes per row.
    /// </summary>
    private static (byte[] data, int stride) CreateSource(int rows, int columns)
    {
        int stride = (columns * 4) + 8;
        byte[] data = new byte[stride * rows];
        Array.Fill(data, PADDING);

        for (int row = 0; row < rows; row++)
        {
            Span<ColorBGRA> pixels = MemoryMarshal.Cast<byte, ColorBGRA>(data.AsSpan(row * stride, columns * 4));
            for (int column = 0; column < columns; column++)
                pixels[column] = S(row, column);
        }

        return (data, stride);
    }

    private static ColorBGRA[] Copy(ImageRotation rotation, int sourceRows, int sourceColumns)
    {
        (byte[] source, int stride) = CreateSource(sourceRows, sourceColumns);
        byte[] target = new byte[WIDTH * HEIGHT * 4];

        CopyRotateHelper.Copy(rotation, source, stride, WIDTH, HEIGHT, target);

        return MemoryMarshal.Cast<byte, ColorBGRA>(target).ToArray();
    }

    [TestMethod]
    public void Rotate0CopiesRowsWithoutPadding()
        => CollectionAssert.AreEqual(new[] { S(0, 0), S(0, 1), S(0, 2),
                                             S(1, 0), S(1, 1), S(1, 2) },
                                     Copy(ImageRotation.None, HEIGHT, WIDTH));

    [TestMethod]
    public void Rotate180()
        => CollectionAssert.AreEqual(new[] { S(1, 2), S(1, 1), S(1, 0),
                                             S(0, 2), S(0, 1), S(0, 0) },
                                     Copy(ImageRotation.Rotate180, HEIGHT, WIDTH));

    [TestMethod]
    public void Rotate90()
        => CollectionAssert.AreEqual(new[] { S(2, 0), S(1, 0), S(0, 0),
                                             S(2, 1), S(1, 1), S(0, 1) },
                                     Copy(ImageRotation.Rotate90, WIDTH, HEIGHT));

    [TestMethod]
    public void Rotate270()
        => CollectionAssert.AreEqual(new[] { S(0, 1), S(1, 1), S(2, 1),
                                             S(0, 0), S(1, 0), S(2, 0) },
                                     Copy(ImageRotation.Rotate270, WIDTH, HEIGHT));

    #endregion
}
