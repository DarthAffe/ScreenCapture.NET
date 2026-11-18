using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using HPPH;

namespace ScreenCapture.NET.Tests.Hardware;

/// <summary>
/// DX11 tests that take over the desktop (they move the mouse pointer), so they only run on request:
/// <c>dotnet test --filter TestCategory=Interactive</c>. Not part of a <c>TestCategory=Hardware</c> run.
/// </summary>
[TestClass]
[TestCategory(HardwareTestHelper.INTERACTIVE_CATEGORY)]
[OSCondition(OperatingSystems.Windows)]
[SupportedOSPlatform("windows")]
public sealed partial class DX11InteractiveTests
{
    /// <summary>
    /// With <see cref="DX11CaptureOptions.DrawCursor"/> the pointer shows in zones under it (also downscaled ones), at its position, and disappears when it moves away,
    /// also to another display. After a restart it shows again without moving.
    /// Checked on every display, so rotated ones show if the position and the shape are in the orientation of the display.
    /// <b>Moves the mouse pointer</b> for about three seconds per display (it's put back afterwards): don't use the mouse meanwhile.
    /// </summary>
    [TestMethod]
    public void DrawsCursorIntoZones()
    {
        // SetCursorPos then takes physical coordinates, like the ones of the display.
        Assert.IsTrue(DPIAwareness.Initialize());

        using DX11ScreenCaptureService service = new(new DX11CaptureOptions { DrawCursor = true, Timeout = TimeSpan.FromMilliseconds(100) });

        Assert.IsTrue(GetCursorPos(out POINT original));
        List<string> skipped = [];
        int checkedDisplays = 0;
        try
        {
            List<Display> displays = [.. HardwareTestHelper.GetAllDisplays(service)];
            for (int i = 0; i < displays.Count; i++)
            {
                Display display = displays[i];
                Display? other = displays.Count > 1 ? displays[(i + 1) % displays.Count] : null;
                POINT? elsewhere = other is { } o ? new POINT(o.X + 2, o.Y + 2) : null;

                string? skipReason = AssertDrawsCursor(service.GetScreenCapture(display), display, elsewhere);
                if (skipReason == null)
                {
                    checkedDisplays++;
                    Console.WriteLine($"{display.DeviceName} ({display.Rotation}): checked.");
                }
                else
                {
                    skipped.Add($"{display.DeviceName}: {skipReason}");
                    Console.WriteLine($"{display.DeviceName} ({display.Rotation}): skipped, {skipReason}");
                }
            }
        }
        finally
        {
            SetCursorPos(original.X, original.Y);
        }

        if (checkedDisplays == 0)
            Assert.Inconclusive(string.Join(" ", skipped));
    }

    // Returns why the display couldn't be checked, or null if it was. elsewhere: a position on another display (null if there's only one).
    private static string? AssertDrawsCursor(DX11ScreenCapture screenCapture, Display display, POINT? elsewhere)
    {
        // Zones tiling the display, except the top left one where the pointer waits. The check uses one that shows a part of the screen that doesn't change.
        const int size = 256;
        List<(CaptureZone<ColorBGRA> Zone, CaptureZone<ColorBGRA> Downscaled)> candidates = [];
        for (int y = 0; (y + size) <= display.Height; y += size)
            for (int x = y == 0 ? size : 0; (x + size) <= display.Width; x += size)
            {
                CaptureRegion candidate = new(x, y, size, size);
                candidates.Add((screenCapture.RegisterCaptureZone(candidate), screenCapture.RegisterCaptureZone(candidate, downscaleLevel: 2)));
            }

        HardwareTestHelper.CaptureFrame(screenCapture);

        POINT away = new(display.X + 2, display.Y + 2);

        MoveCursor(screenCapture, away);
        ColorBGRA[][] first = [.. candidates.Select(c => HardwareTestHelper.GetPixels(c.Zone))];
        MoveCursor(screenCapture, away);
        int index = Enumerable.Range(0, candidates.Count).FirstOrDefault(i => HardwareTestHelper.GetPixels(candidates[i].Zone).AsSpan().SequenceEqual(first[i]), -1);
        if (index < 0) return "no part of the screen stood still.";

        (CaptureZone<ColorBGRA> zone, CaptureZone<ColorBGRA> downscaled) = candidates[index];
        CaptureRegion region = zone.Region;
        (ColorBGRA[] pixels, ColorBGRA[] downscaledPixels) without = (HardwareTestHelper.GetPixels(zone), HardwareTestHelper.GetPixels(downscaled));

        MoveCursor(screenCapture, new POINT(display.X + region.X + (size / 2), display.Y + region.Y + (size / 2)));
        (ColorBGRA[] pixels, ColorBGRA[] downscaledPixels) with = (HardwareTestHelper.GetPixels(zone), HardwareTestHelper.GetPixels(downscaled));

        // A restart forgets the pointer (like one after the secure desktop or a mode change): the new duplication has to deliver it again, without a move.
        screenCapture.Restart();
        CaptureFor(screenCapture, TimeSpan.FromMilliseconds(500));
        ColorBGRA[] afterRestart = HardwareTestHelper.GetPixels(zone);

        // On another display the pointer isn't visible for this one.
        ColorBGRA[]? movedElsewhere = null;
        if (elsewhere is { } position)
        {
            MoveCursor(screenCapture, position);
            movedElsewhere = HardwareTestHelper.GetPixels(zone);
        }

        MoveCursor(screenCapture, away);
        (ColorBGRA[] pixels, ColorBGRA[] downscaledPixels) withoutAgain = (HardwareTestHelper.GetPixels(zone), HardwareTestHelper.GetPixels(downscaled));

        if (!without.pixels.AsSpan().SequenceEqual(withoutAgain.pixels)) return "the screen under the zone changed while the pointer was moved.";

        string name = $"{display.DeviceName} ({display.Rotation}), zone {region}";

        // The top left of the drawn shape is the position minus the hot spot (which lies within the shape, up to 64 pixels for usual pointer sizes).
        (int left, int top)? drawn = null;
        for (int i = 0; i < with.pixels.Length; i++)
            if (with.pixels[i] != without.pixels[i])
                drawn = (Math.Min(drawn?.left ?? int.MaxValue, i % size), Math.Min(drawn?.top ?? int.MaxValue, i / size));

        Assert.IsNotNull(drawn, $"{name}: the pointer isn't drawn into the zone.");
        Assert.IsInRange((size / 2) - 64, size / 2, drawn.Value.left, $"{name}: the pointer is drawn at the wrong horizontal position.");
        Assert.IsInRange((size / 2) - 64, size / 2, drawn.Value.top, $"{name}: the pointer is drawn at the wrong vertical position.");

        Assert.IsFalse(with.downscaledPixels.AsSpan().SequenceEqual(without.downscaledPixels), $"{name}: the pointer isn't drawn into the downscaled zone.");
        CollectionAssert.AreEqual(without.downscaledPixels, withoutAgain.downscaledPixels, $"{name}: the pointer is still drawn into the downscaled zone after it moved away.");

        CollectionAssert.AreEqual(with.pixels, afterRestart, $"{name}: the pointer isn't drawn (as before) after a restart.");
        if (movedElsewhere != null)
            CollectionAssert.AreEqual(without.pixels, movedElsewhere, $"{name}: the pointer is still drawn after it moved to another display.");

        foreach ((CaptureZone<ColorBGRA> candidate, CaptureZone<ColorBGRA> downscaledCandidate) in candidates)
        {
            screenCapture.UnregisterCaptureZone(candidate);
            screenCapture.UnregisterCaptureZone(downscaledCandidate);
        }

        return null;
    }

    // Moves the pointer and captures for a while, so the zones show it at its new place (also if something else on the screen renders all the time).
    private static void MoveCursor(DX11ScreenCapture screenCapture, POINT position)
    {
        Assert.IsTrue(SetCursorPos(position.X, position.Y));
        CaptureFor(screenCapture, TimeSpan.FromMilliseconds(300));
    }

    private static void CaptureFor(DX11ScreenCapture screenCapture, TimeSpan duration)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < duration)
            screenCapture.CaptureScreen();
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct POINT(int X, int Y);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetCursorPos(out POINT point);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetCursorPos(int x, int y);
}
