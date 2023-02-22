using System.Runtime.CompilerServices;

namespace ScreenCapture.NET;

public static class MathHelper
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Clamp(this float value, float min, float max)
    {
        // ReSharper disable ConvertIfStatementToReturnStatement - I'm not sure why, but inlining this statement reduces performance by ~10%
        if (value < min) return min;
        if (value > max) return max;
        return value;
        // ReSharper restore ConvertIfStatementToReturnStatement
    }

    /// <summary>
    /// Converts a normalized float value in the range [0..1] to a byte [0..255].
    /// </summary>
    /// <param name="percentage">The normalized float value to convert.</param>
    /// <returns>The byte value.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte GetByteValueFromPercentage(this float percentage)
    {
        if (float.IsNaN(percentage)) return 0;

        percentage = percentage.Clamp(0, 1.0f);
        return (byte)(percentage >= 1.0f ? 255 : percentage * 256.0f);
    }

    /// <summary>
    /// Converts a byte value [0..255] to a normalized float value in the range [0..1].
    /// </summary>
    /// <param name="value">The byte value to convert.</param>
    /// <returns>The normalized float value.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float GetPercentageFromByteValue(this byte value)
        => value == 255 ? 1.0f : (value / 256.0f);
}