using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using HPPH;

namespace ScreenCapture.NET.Tests.Hardware;

/// <summary>
/// The mouse pointer checks of the interactive tests, shared by the Windows backends. They <b>move the mouse pointer</b> (it's put back afterwards).
/// </summary>
[SupportedOSPlatform("windows")]
internal static partial class InteractiveTestHelper
{
    #region Constants

    // The size of the zones the pointer is checked in.
    private const int SIZE = 256;

    #endregion

    #region Methods

    /// <summary>
    /// Checks on every display that the pointer shows in zones under it at its position, disappears when it moves away, and shows again after a restart.
    /// Inconclusive if nothing stood still.
    /// </summary>
    /// <param name="service">The service of the captures; created with the option that draws the pointer.</param>
    /// <param name="getScreenCapture">Gets the capture of a display from the service.</param>
    public static void AssertDrawsCursorOnEveryDisplay(IScreenCaptureService service, Func<Display, AbstractScreenCapture<ColorBGRA>> getScreenCapture)
    {
        // SetCursorPos then takes physical coordinates, like the ones of the display.
        Assert.IsTrue(DPIAwareness.Initialize());

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

                string? skipReason = AssertDrawsCursor(getScreenCapture(display), display, elsewhere);
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

    /// <summary>
    /// Checks that the pointer doesn't show in a zone under it, on the first display where a part of the screen stands still. Inconclusive if none does.
    /// </summary>
    /// <param name="service">The service of the captures; created without the option that draws the pointer.</param>
    /// <param name="getScreenCapture">Gets the capture of a display from the service.</param>
    public static void AssertCursorIsNotDrawn(IScreenCaptureService service, Func<Display, AbstractScreenCapture<ColorBGRA>> getScreenCapture)
    {
        Assert.IsTrue(DPIAwareness.Initialize());

        Assert.IsTrue(GetCursorPos(out POINT original));
        List<string> skipped = [];
        try
        {
            foreach (Display display in HardwareTestHelper.GetAllDisplays(service))
            {
                AbstractScreenCapture<ColorBGRA> screenCapture = getScreenCapture(display);
                List<CaptureZone<ColorBGRA>> candidates = RegisterCandidates(screenCapture, display);
                HardwareTestHelper.CaptureFrame(screenCapture);

                POINT away = new(display.X + 2, display.Y + 2);
                if (FindStaticZone(screenCapture, candidates, away) is not { } zone)
                {
                    skipped.Add($"{display.DeviceName}: no part of the screen stood still.");
                    Unregister(screenCapture, candidates);
                    continue;
                }

                ColorBGRA[] without = HardwareTestHelper.GetPixels(zone);
                MoveCursor(screenCapture, new POINT(display.X + zone.Region.X + (SIZE / 2), display.Y + zone.Region.Y + (SIZE / 2)));
                ColorBGRA[] under = HardwareTestHelper.GetPixels(zone);
                MoveCursor(screenCapture, away);
                ColorBGRA[] withoutAgain = HardwareTestHelper.GetPixels(zone);
                Unregister(screenCapture, candidates);

                if (!without.AsSpan().SequenceEqual(withoutAgain))
                {
                    skipped.Add($"{display.DeviceName}: the screen under the zone changed while the pointer was moved.");
                    continue;
                }

                CollectionAssert.AreEqual(without, under, $"{display.DeviceName} ({display.Rotation}), zone {zone.Region}: the pointer is drawn without the option.");
                Console.WriteLine($"{display.DeviceName} ({display.Rotation}): checked.");
                return;
            }
        }
        finally
        {
            SetCursorPos(original.X, original.Y);
        }

        Assert.Inconclusive(string.Join(" ", skipped));
    }

    // Returns why the display couldn't be checked, or null if it was. elsewhere: a position on another display (null if there's only one).
    private static string? AssertDrawsCursor(AbstractScreenCapture<ColorBGRA> screenCapture, Display display, POINT? elsewhere)
    {
        List<CaptureZone<ColorBGRA>> candidates = RegisterCandidates(screenCapture, display);
        List<CaptureZone<ColorBGRA>> downscaledCandidates = [.. candidates.Select(c => screenCapture.RegisterCaptureZone(c.Region, downscaleLevel: 2))];
        try
        {
            HardwareTestHelper.CaptureFrame(screenCapture);

            POINT away = new(display.X + 2, display.Y + 2);
            if (FindStaticZone(screenCapture, candidates, away) is not { } zone) return "no part of the screen stood still.";

            CaptureZone<ColorBGRA> downscaled = downscaledCandidates[candidates.IndexOf(zone)];
            CaptureRegion region = zone.Region;
            (ColorBGRA[] pixels, ColorBGRA[] downscaledPixels) without = (HardwareTestHelper.GetPixels(zone), HardwareTestHelper.GetPixels(downscaled));

            MoveCursor(screenCapture, new POINT(display.X + region.X + (SIZE / 2), display.Y + region.Y + (SIZE / 2)));
            (ColorBGRA[] pixels, ColorBGRA[] downscaledPixels) with = (HardwareTestHelper.GetPixels(zone), HardwareTestHelper.GetPixels(downscaled));

            // A restart forgets the pointer, the new capture has to deliver it without a move.
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

            // The shape starts at the position minus the hot spot (up to 64 px for usual pointers).
            (int left, int top)? drawn = null;
            for (int i = 0; i < with.pixels.Length; i++)
                if (with.pixels[i] != without.pixels[i])
                    drawn = (Math.Min(drawn?.left ?? int.MaxValue, i % SIZE), Math.Min(drawn?.top ?? int.MaxValue, i / SIZE));

            Assert.IsNotNull(drawn, $"{name}: the pointer isn't drawn into the zone.");
            Assert.IsInRange((SIZE / 2) - 64, SIZE / 2, drawn.Value.left, $"{name}: the pointer is drawn at the wrong horizontal position.");
            Assert.IsInRange((SIZE / 2) - 64, SIZE / 2, drawn.Value.top, $"{name}: the pointer is drawn at the wrong vertical position.");

            Assert.IsFalse(with.downscaledPixels.AsSpan().SequenceEqual(without.downscaledPixels), $"{name}: the pointer isn't drawn into the downscaled zone.");
            CollectionAssert.AreEqual(without.downscaledPixels, withoutAgain.downscaledPixels, $"{name}: the pointer is still drawn into the downscaled zone after it moved away.");

            CollectionAssert.AreEqual(with.pixels, afterRestart, $"{name}: the pointer isn't drawn (as before) after a restart.");
            if (movedElsewhere != null)
                CollectionAssert.AreEqual(without.pixels, movedElsewhere, $"{name}: the pointer is still drawn after it moved to another display.");

            return null;
        }
        finally
        {
            Unregister(screenCapture, candidates);
            Unregister(screenCapture, downscaledCandidates);
        }
    }

    // Zones tiling the display, except the top left one where the pointer waits. The checks use one that shows a part of the screen that doesn't change.
    private static List<CaptureZone<ColorBGRA>> RegisterCandidates(AbstractScreenCapture<ColorBGRA> screenCapture, Display display)
    {
        List<CaptureZone<ColorBGRA>> candidates = [];
        for (int y = 0; (y + SIZE) <= display.Height; y += SIZE)
            for (int x = y == 0 ? SIZE : 0; (x + SIZE) <= display.Width; x += SIZE)
                candidates.Add(screenCapture.RegisterCaptureZone(new CaptureRegion(x, y, SIZE, SIZE)));

        return candidates;
    }

    // The first zone whose pixels stay the same while the pointer waits at 'away'; null if none does.
    private static CaptureZone<ColorBGRA>? FindStaticZone(AbstractScreenCapture<ColorBGRA> screenCapture, List<CaptureZone<ColorBGRA>> candidates, POINT away)
    {
        MoveCursor(screenCapture, away);
        ColorBGRA[][] first = [.. candidates.Select(HardwareTestHelper.GetPixels)];
        MoveCursor(screenCapture, away);

        int index = Enumerable.Range(0, candidates.Count).FirstOrDefault(i => HardwareTestHelper.GetPixels(candidates[i]).AsSpan().SequenceEqual(first[i]), -1);
        return index < 0 ? null : candidates[index];
    }

    private static void Unregister(AbstractScreenCapture<ColorBGRA> screenCapture, List<CaptureZone<ColorBGRA>> zones)
    {
        foreach (CaptureZone<ColorBGRA> zone in zones)
            screenCapture.UnregisterCaptureZone(zone);
    }

    // Moves the pointer and captures for a while, so the zones show it at its new place.
    private static void MoveCursor(AbstractScreenCapture<ColorBGRA> screenCapture, POINT position)
    {
        Assert.IsTrue(SetCursorPos(position.X, position.Y));
        CaptureFor(screenCapture, TimeSpan.FromMilliseconds(300));
    }

    private static void CaptureFor(AbstractScreenCapture<ColorBGRA> screenCapture, TimeSpan duration)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < duration)
            screenCapture.CaptureScreen();
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetCursorPos(out POINT point);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetCursorPos(int x, int y);

    #endregion

    #region Types

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct POINT(int X, int Y);

    #endregion
}
