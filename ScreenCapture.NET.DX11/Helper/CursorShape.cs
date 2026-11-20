using System;
using HPPH;
using Vortice.DXGI;

namespace ScreenCapture.NET;

/// <summary>
/// The shape of the mouse pointer as Desktop Duplication delivers it, prepared to be drawn into zones (also downscaled ones).
/// </summary>
/// <remarks>
/// Each pixel of the shape changes each color channel of the pixel under it to <c>(add + multiply * value) / 255</c>.
/// Alpha blending (<c>multiply = 255 - alpha</c>), replacing (0), keeping (255) and inverting (-255) are all of this form,
/// so the pixels of a block of a downscaled zone can be averaged like the frame is.
/// </remarks>
internal sealed class CursorShape
{
    #region Constants

    private const int CHANNELS = 3; // B, G, R; the alpha of the frame is kept.

    private const short KEEP = 255;
    private const short INVERT = -255;
    private const int WHITE = 255 * 255;

    #endregion

    #region Properties & Fields

    private readonly int[] _add;
    private readonly short[] _multiply;

    /// <summary>
    /// Gets the width of the shape in pixels.
    /// </summary>
    public int Width { get; }

    /// <summary>
    /// Gets the height of the shape in pixels (for a monochrome pointer half the height Desktop Duplication reports, which includes both masks).
    /// </summary>
    public int Height { get; }

    #endregion

    #region Constructors

    private CursorShape(int width, int height)
    {
        this.Width = width;
        this.Height = height;

        _add = new int[width * height * CHANNELS];
        _multiply = new short[width * height * CHANNELS];
    }

    #endregion

    #region Methods

    /// <summary>
    /// Creates the shape from the data of <c>IDXGIOutputDuplication::GetFramePointerShape</c>.
    /// </summary>
    /// <remarks>
    /// Color pointers are alpha blended. Monochrome pointers (an AND mask over an XOR mask) keep, blacken, whiten or invert pixels.
    /// Masked color pointers replace pixels (alpha 0) or XOR them (otherwise); a channel XORed with 128 or more is inverted, with less kept,
    /// which is exact for the usual 0 and 255.
    /// </remarks>
    /// <param name="type">The type of the shape (<c>DXGI_OUTDUPL_POINTER_SHAPE_INFO::Type</c>).</param>
    /// <param name="width">The width in pixels.</param>
    /// <param name="height">The height in rows of the data (for monochrome pointers both masks).</param>
    /// <param name="pitch">The width of a row of the data in bytes.</param>
    /// <param name="data">The data.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="type"/> is unknown, or a size is negative or too small.</exception>
    /// <exception cref="ArgumentException"><paramref name="data"/> is too small.</exception>
    public static CursorShape Create(PointerShapeType type, int width, int height, int pitch, ReadOnlySpan<byte> data)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(width);
        ArgumentOutOfRangeException.ThrowIfNegative(height);
        ArgumentOutOfRangeException.ThrowIfLessThan(pitch, type == PointerShapeType.Monochrome ? (width + 7) / 8 : width * 4);

        if (data.Length < ((long)pitch * height))
            throw new ArgumentException($"The data has {data.Length} bytes, but {height} rows of {pitch} bytes need {(long)pitch * height}.", nameof(data));

        return type switch
        {
            PointerShapeType.Color => CreateColor(width, height, pitch, data),
            PointerShapeType.Monochrome => CreateMonochrome(width, height / 2, pitch, data),
            PointerShapeType.MaskedColor => CreateMaskedColor(width, height, pitch, data),
            _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown pointer shape type.")
        };
    }

    private static CursorShape CreateColor(int width, int height, int pitch, ReadOnlySpan<byte> data)
    {
        CursorShape shape = new(width, height);
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                ReadOnlySpan<byte> pixel = data.Slice((y * pitch) + (x * 4), 4);
                byte alpha = pixel[3];
                for (int channel = 0; channel < CHANNELS; channel++)
                    shape.Set(x, y, channel, pixel[channel] * alpha, (short)(255 - alpha));
            }

        return shape;
    }

    private static CursorShape CreateMonochrome(int width, int height, int pitch, ReadOnlySpan<byte> data)
    {
        CursorShape shape = new(width, height);
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int mask = 0x80 >> (x % 8);
                bool and = (data[(y * pitch) + (x / 8)] & mask) != 0;
                bool xor = (data[((y + height) * pitch) + (x / 8)] & mask) != 0;

                (int add, short multiply) = (and, xor) switch
                {
                    (true, false) => (0, KEEP),
                    (true, true) => (WHITE, INVERT),
                    (false, false) => (0, (short)0),
                    (false, true) => (WHITE, (short)0)
                };

                for (int channel = 0; channel < CHANNELS; channel++)
                    shape.Set(x, y, channel, add, multiply);
            }

        return shape;
    }

    private static CursorShape CreateMaskedColor(int width, int height, int pitch, ReadOnlySpan<byte> data)
    {
        CursorShape shape = new(width, height);
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                ReadOnlySpan<byte> pixel = data.Slice((y * pitch) + (x * 4), 4);
                bool xor = pixel[3] != 0;
                for (int channel = 0; channel < CHANNELS; channel++)
                {
                    byte value = pixel[channel];
                    if (!xor) shape.Set(x, y, channel, value * 255, 0);
                    else if (value >= 128) shape.Set(x, y, channel, WHITE, INVERT);
                    else shape.Set(x, y, channel, 0, KEEP);
                }
            }

        return shape;
    }

    private void Set(int x, int y, int channel, int add, short multiply)
    {
        int index = (((y * Width) + x) * CHANNELS) + channel;
        _add[index] = add;
        _multiply[index] = multiply;
    }

    /// <summary>
    /// Draws the pointer into the pixels of a zone.
    /// </summary>
    /// <remarks>
    /// The blocks of a downscaled zone are the ones of <see cref="CaptureZoneWriter.Write{TColor}"/>: 2^level x 2^level pixels from the top left of the region,
    /// a region smaller than a block is one block. The pointer is averaged over each block it covers (pixels it doesn't cover are kept) and blended into the zone's pixel,
    /// which is close to downscaling the frame with the pointer drawn into it.
    /// </remarks>
    /// <param name="buffer">The pixels of the zone, in the orientation of the display, <paramref name="width"/> per row.</param>
    /// <param name="region">The region of the zone on the display.</param>
    /// <param name="width">The width of the zone (after downscaling).</param>
    /// <param name="height">The height of the zone (after downscaling).</param>
    /// <param name="downscaleLevel">The downscale level of the zone.</param>
    /// <param name="x">The left edge of the pointer on the display (it can lie outside of it).</param>
    /// <param name="y">The top edge of the pointer on the display (it can lie outside of it).</param>
    public void Draw(Span<ColorBGRA> buffer, CaptureRegion region, int width, int height, int downscaleLevel, int x, int y)
    {
        if ((Width == 0) || (Height == 0) || (width <= 0) || (height <= 0)) return;

        int factor = 1 << downscaleLevel;
        int blockWidth = Math.Min(factor, region.Width);
        int blockHeight = Math.Min(factor, region.Height);

        // The pointer relative to the region.
        int left = x - region.X;
        int top = y - region.Y;

        int firstColumn = Math.Max(0, FloorDivide(left, blockWidth));
        int lastColumn = Math.Min(width - 1, FloorDivide((left + Width) - 1, blockWidth));
        int firstRow = Math.Max(0, FloorDivide(top, blockHeight));
        int lastRow = Math.Min(height - 1, FloorDivide((top + Height) - 1, blockHeight));

        long blockSize = (long)blockWidth * blockHeight;
        Span<long> add = stackalloc long[CHANNELS];
        Span<long> multiply = stackalloc long[CHANNELS];

        for (int row = firstRow; row <= lastRow; row++)
        {
            int blockTop = row * blockHeight;
            int shapeTop = Math.Max(blockTop, top) - top;
            int shapeBottom = Math.Min(blockTop + blockHeight, top + Height) - top;

            for (int column = firstColumn; column <= lastColumn; column++)
            {
                int blockLeft = column * blockWidth;
                int shapeLeft = Math.Max(blockLeft, left) - left;
                int shapeRight = Math.Min(blockLeft + blockWidth, left + Width) - left;

                // The pixels of the block the pointer doesn't cover are kept.
                long kept = blockSize - ((long)(shapeRight - shapeLeft) * (shapeBottom - shapeTop));
                for (int channel = 0; channel < CHANNELS; channel++)
                {
                    add[channel] = 0;
                    multiply[channel] = kept * KEEP;
                }

                for (int shapeY = shapeTop; shapeY < shapeBottom; shapeY++)
                {
                    int index = ((shapeY * Width) + shapeLeft) * CHANNELS;
                    for (int shapeX = shapeLeft; shapeX < shapeRight; shapeX++)
                        for (int channel = 0; channel < CHANNELS; channel++, index++)
                        {
                            add[channel] += _add[index];
                            multiply[channel] += _multiply[index];
                        }
                }

                ref ColorBGRA pixel = ref buffer[(row * width) + column];
                pixel = new ColorBGRA(Blend(pixel.B, add[0], multiply[0], blockSize),
                                      Blend(pixel.G, add[1], multiply[1], blockSize),
                                      Blend(pixel.R, add[2], multiply[2], blockSize),
                                      pixel.A);
            }
        }
    }

    private static byte Blend(byte value, long add, long multiply, long count)
    {
        long divisor = count * 255;
        long result = ((add + (multiply * value)) + (divisor / 2)) / divisor;
        return (byte)Math.Clamp(result, 0, 255);
    }

    private static int FloorDivide(int value, int divisor) => (int)Math.Floor((double)value / divisor);

    #endregion
}
