using System;
using System.Runtime.InteropServices;
using HPPH;

namespace ScreenCapture.NET.Tests;

internal class TestScreenCapture : AbstractScreenCapture<ColorARGB>
{
    #region Properties & Fields

    /// <summary>
    /// Called at the start of every zone update (with the zone lock held).
    /// </summary>
    public Action<CaptureZone<ColorARGB>>? ZoneUpdating { get; set; }

    /// <summary>
    /// Gets or sets the color written to each pixel of a zone (by its location in the zone).
    /// </summary>
    public Func<int, int, ColorARGB> Pattern { get; set; } = GetColorFromLocation;

    /// <summary>
    /// Gets or sets if zone updates write their buffer. If <c>false</c>, they report that there was nothing to write.
    /// </summary>
    public bool WriteZones { get; set; } = true;

    #endregion

    #region Constructors

    public TestScreenCapture(Rotation rotation = Rotation.None)
        : base(new Display(0, "Test", 1920, 1080, rotation, new GraphicsCard(0, "Test", 0, 0)))
    { }

    #endregion

    #region Methods

    protected override bool PerformScreenCapture() => true;

    protected override bool PerformCaptureZoneUpdate(CaptureZone<ColorARGB> captureZone, Span<byte> buffer)
    {
        ZoneUpdating?.Invoke(captureZone);
        if (!WriteZones) return false;

        Func<int, int, ColorARGB> pattern = Pattern;

        Span<ColorARGB> pixels = MemoryMarshal.Cast<byte, ColorARGB>(buffer);

        for (int y = 0; y < captureZone.Height; y++)
            for (int x = 0; x < captureZone.Width; x++)
                pixels[(y * captureZone.Width) + x] = pattern(x, y);

        return true;
    }

    public static ColorARGB GetColorFromLocation(int x, int y)
    {
        byte[] xBytes = BitConverter.GetBytes((short)x);
        byte[] yBytes = BitConverter.GetBytes((short)y);
        return new ColorARGB(xBytes[0], xBytes[1], yBytes[0], yBytes[1]);
    }

    #endregion
}