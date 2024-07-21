using System;
using System.Runtime.InteropServices;
using HPPH;

namespace ScreenCapture.NET.Tests;

internal class TestScreenCapture : AbstractScreenCapture<ColorARGB>
{
    #region Constructors

    public TestScreenCapture(Rotation rotation = Rotation.None)
        : base(new Display(0, "Test", 1920, 1080, rotation, new GraphicsCard(0, "Test", 0, 0)))
    { }

    #endregion

    #region Methods

    protected override bool PerformScreenCapture() => true;

    protected override void PerformCaptureZoneUpdate(CaptureZone<ColorARGB> captureZone, Span<byte> buffer)
    {
        Span<ColorARGB> pixels = MemoryMarshal.Cast<byte, ColorARGB>(buffer);

        for (int y = 0; y < captureZone.Height; y++)
            for (int x = 0; x < captureZone.Width; x++)
                pixels[(y * captureZone.Width) + x] = GetColorFromLocation(x, y);
    }

    public static ColorARGB GetColorFromLocation(int x, int y)
    {
        byte[] xBytes = BitConverter.GetBytes((short)x);
        byte[] yBytes = BitConverter.GetBytes((short)y);
        return new ColorARGB(xBytes[0], xBytes[1], yBytes[0], yBytes[1]);
    }

    #endregion
}