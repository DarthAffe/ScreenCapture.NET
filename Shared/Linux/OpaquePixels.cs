using System;
using System.Numerics;
using System.Runtime.InteropServices;
using HPPH;

namespace ScreenCapture.NET;

/// <summary>
/// Makes pixels whose fourth byte is padding (X11 depth-24, PipeWire BGRx) opaque.
/// </summary>
internal static class OpaquePixels
{
    /// <summary>
    /// Sets the alpha of every pixel to 255.
    /// </summary>
    public static void SetOpaque(Span<ColorBGRA> pixels)
    {
        // ColorBGRA is stored as the bytes B, G, R, A.
        uint alpha = BitConverter.IsLittleEndian ? 0xFF000000 : 0xFF;
        Span<uint> values = MemoryMarshal.Cast<ColorBGRA, uint>(pixels);

        int i = 0;
        if (Vector.IsHardwareAccelerated)
        {
            Vector<uint> alphaVector = new(alpha);
            for (; i <= (values.Length - Vector<uint>.Count); i += Vector<uint>.Count)
            {
                Span<uint> block = values.Slice(i, Vector<uint>.Count);
                (new Vector<uint>(block) | alphaVector).CopyTo(block);
            }
        }

        for (; i < values.Length; i++)
            values[i] |= alpha;
    }
}
