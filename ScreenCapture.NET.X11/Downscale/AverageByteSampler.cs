// DarthAffe 07.09.2023: Copied from https://github.com/DarthAffe/RGB.NET/blob/2e0754f474b82ed4d0cae5c6c44378d234f1321b/RGB.NET.Presets/Textures/Sampler/AverageByteSampler.cs

using System;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace ScreenCapture.NET.Downscale;

/// <summary>
/// Represents a sampled that averages multiple byte-data entries.
/// </summary>
internal static class AverageByteSampler
{
    #region Constants

    private static readonly int INT_VECTOR_LENGTH = Vector<uint>.Count;

    #endregion

    #region Methods

    public static unsafe void Sample(in SamplerInfo<byte> info, in Span<byte> pixelData)
    {
        int count = info.Width * info.Height;
        if (count == 0) return;

        int dataLength = pixelData.Length;
        Span<uint> sums = stackalloc uint[dataLength];

        int elementsPerVector = Vector<byte>.Count / dataLength;
        int valuesPerVector = elementsPerVector * dataLength;
        if (Vector.IsHardwareAccelerated && (info.Height > 1) && (info.Width >= valuesPerVector) && (dataLength <= Vector<byte>.Count))
        {
            int chunks = info.Width / elementsPerVector;

            Vector<uint> sum1 = Vector<uint>.Zero;
            Vector<uint> sum2 = Vector<uint>.Zero;
            Vector<uint> sum3 = Vector<uint>.Zero;
            Vector<uint> sum4 = Vector<uint>.Zero;

            for (int y = 0; y < info.Height; y++)
            {
                ReadOnlySpan<byte> data = info[y];

                fixed (byte* colorPtr = data)
                {
                    byte* current = colorPtr;
                    for (int i = 0; i < chunks; i++)
                    {
                        Vector<byte> bytes = *(Vector<byte>*)current;
                        Vector.Widen(bytes, out Vector<ushort> short1, out Vector<ushort> short2);
                        Vector.Widen(short1, out Vector<uint> int1, out Vector<uint> int2);
                        Vector.Widen(short2, out Vector<uint> int3, out Vector<uint> int4);

                        sum1 = Vector.Add(sum1, int1);
                        sum2 = Vector.Add(sum2, int2);
                        sum3 = Vector.Add(sum3, int3);
                        sum4 = Vector.Add(sum4, int4);

                        current += valuesPerVector;
                    }
                }

                int missingElements = data.Length - (chunks * valuesPerVector);
                int offset = chunks * valuesPerVector;
                for (int i = 0; i < missingElements; i += dataLength)
                    for (int j = 0; j < sums.Length; j++)
                        sums[j] += data[offset + i + j];
            }

            int value = 0;
            int sumIndex = 0;
            for (int j = 0; (j < INT_VECTOR_LENGTH) && (value < valuesPerVector); j++)
            {
                sums[sumIndex] += sum1[j];
                ++sumIndex;
                ++value;

                if (sumIndex >= dataLength)
                    sumIndex = 0;
            }

            for (int j = 0; (j < INT_VECTOR_LENGTH) && (value < valuesPerVector); j++)
            {
                sums[sumIndex] += sum2[j];
                ++sumIndex;
                ++value;

                if (sumIndex >= dataLength)
                    sumIndex = 0;
            }

            for (int j = 0; (j < INT_VECTOR_LENGTH) && (value < valuesPerVector); j++)
            {
                sums[sumIndex] += sum3[j];
                ++sumIndex;
                ++value;

                if (sumIndex >= dataLength)
                    sumIndex = 0;
            }

            for (int j = 0; (j < INT_VECTOR_LENGTH) && (value < valuesPerVector); j++)
            {
                sums[sumIndex] += sum4[j];
                ++sumIndex;
                ++value;

                if (sumIndex >= dataLength)
                    sumIndex = 0;
            }
        }
        else
        {
            for (int y = 0; y < info.Height; y++)
            {
                ReadOnlySpan<byte> data = info[y];
                for (int i = 0; i < data.Length; i += dataLength)
                    for (int j = 0; j < sums.Length; j++)
                        sums[j] += data[i + j];
            }
        }

        float divisor = count * byte.MaxValue;
        for (int i = 0; i < pixelData.Length; i++)
            pixelData[i] = (sums[i] / divisor).GetByteValueFromPercentage();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte GetByteValueFromPercentage(this float percentage)
    {
        if (float.IsNaN(percentage)) return 0;

        percentage = percentage.Clamp(0, 1.0f);
        return (byte)(percentage >= 1.0f ? 255 : percentage * 256.0f);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float Clamp(this float value, float min, float max)
    {
        // ReSharper disable ConvertIfStatementToReturnStatement - I'm not sure why, but inlining this statement reduces performance by ~10%
        if (value < min) return min;
        if (value > max) return max;
        return value;
        // ReSharper restore ConvertIfStatementToReturnStatement
    }

    #endregion
}