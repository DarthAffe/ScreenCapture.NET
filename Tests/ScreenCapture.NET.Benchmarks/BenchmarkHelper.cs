using System.Runtime.InteropServices;
using HPPH;

namespace ScreenCapture.NET.Benchmarks;

public enum Resolution
{
    /// <summary>1920x1080.</summary>
    FullHD,

    /// <summary>5120x1440 (32:9).</summary>
    DualWQHD
}

internal static class BenchmarkHelper
{
    public static (int width, int height) GetSize(Resolution resolution)
        => resolution switch
        {
            Resolution.FullHD => (1920, 1080),
            Resolution.DualWQHD => (5120, 1440),
            _ => throw new ArgumentOutOfRangeException(nameof(resolution))
        };

    /// <summary>
    /// Creates a BGRA frame with non-black, varying content and the given row stride (padding bytes are left 0).
    /// </summary>
    public static byte[] CreateFrame(int width, int height, int stride)
    {
        byte[] data = new byte[stride * height];
        for (int y = 0; y < height; y++)
        {
            Span<ColorBGRA> row = MemoryMarshal.Cast<byte, ColorBGRA>(data.AsSpan(y * stride, width * 4));
            for (int x = 0; x < width; x++)
                row[x] = new ColorBGRA((byte)(32 + (x % 200)), (byte)(32 + (y % 200)), (byte)(32 + ((x + y) % 200)), 255);
        }

        return data;
    }

    public static int Align(int value, int alignment) => ((value + alignment - 1) / alignment) * alignment;
}

/// <summary>
/// A capture without a backend, used to create zones with the real size calculation.
/// </summary>
internal sealed class BenchmarkScreenCapture(int width, int height)
    : AbstractScreenCapture<ColorBGRA>(new Display("Benchmark", 0, "Benchmark", string.Empty, 0, 0, width, height, ImageRotation.None, new GraphicsCard("Benchmark", 0, "Benchmark", 0, 0)))
{
    protected override CaptureResult PerformScreenCapture(out long timestamp)
    {
        timestamp = 0;
        return CaptureResult.NewFrame;
    }

    protected override bool PerformCaptureZoneUpdate(CaptureZone<ColorBGRA> captureZone, Span<ColorBGRA> buffer) => true;

    protected override bool PerformRestart() => true;
}
