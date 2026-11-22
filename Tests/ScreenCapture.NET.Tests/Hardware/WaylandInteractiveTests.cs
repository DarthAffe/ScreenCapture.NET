extern alias Wayland;
using System.Diagnostics;
using System.Runtime.Versioning;
using HPPH;
using Wayland::ScreenCapture.NET;

namespace ScreenCapture.NET.Tests.Hardware;

/// <summary>
/// Wayland tests with the real ScreenCast portal: the compositor shows its share dialog, which the user has to answer (share at least one monitor).
/// They only run on request: <c>dotnet test --filter TestCategory=Interactive</c>, one TFM at a time (<c>-f</c>), in a Wayland session.
/// Not part of a <c>TestCategory=Hardware</c> run.
/// <para>
/// The restore token is stored in <c>$XDG_CACHE_HOME/ScreenCapture.NET.Tests/wayland-restore-token</c>, so later runs skip the dialog.
/// Delete the file (or revoke the permission) to see it again.
/// </para>
/// </summary>
[TestClass]
[TestCategory(HardwareTestHelper.INTERACTIVE_CATEGORY)]
[OSCondition(OperatingSystems.Linux)]
[SupportedOSPlatform("linux")]
public sealed class WaylandInteractiveTests
{
    private static string TokenFile
        => Path.Combine(Environment.GetEnvironmentVariable("XDG_CACHE_HOME") is { Length: > 0 } cache ? cache : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache"),
                        "ScreenCapture.NET.Tests", "wayland-restore-token");

    [TestInitialize]
    public void RequireWayland()
    {
        if (Environment.GetEnvironmentVariable("WAYLAND_DISPLAY") == null)
            Assert.Inconclusive("Not in a Wayland session.");
    }

    // With the stored restore token, which is replaced when the portal hands out a new one.
    private static WaylandScreenCaptureService CreateService(bool drawCursor = false)
    {
        string? token = File.Exists(TokenFile) ? File.ReadAllText(TokenFile).Trim() : null;
        WaylandScreenCaptureService service = new(sessionOptions: new WaylandSessionOptions { DrawCursor = drawCursor, RestoreToken = token });
        service.RestoreTokenChanged += (_, args) =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(TokenFile)!);
            File.WriteAllText(TokenFile, args.RestoreToken);
        };

        return service;
    }

    /// <summary>
    /// <b>Shows the share dialog once</b>: share one or more monitors. Later calls return the same displays without a dialog.
    /// </summary>
    [TestMethod]
    public void DisplaysAreTheSharedMonitors()
    {
        using WaylandScreenCaptureService service = CreateService();
        GraphicsCard graphicsCard = service.GetGraphicsCards().Single();

        List<Display> displays = [.. service.GetDisplays(graphicsCard)];
        Assert.IsNotEmpty(displays);
        foreach (Display display in displays)
            Assert.IsTrue((display.Width > 0) && (display.Height > 0), $"{display.Id} has no size.");

        Assert.AreEqual(displays.Count, displays.Select(display => display.Id).Distinct().Count(), "The ids aren't unique.");

        Stopwatch stopwatch = Stopwatch.StartNew();
        CollectionAssert.AreEqual(displays, service.GetDisplays(graphicsCard).ToList());
        Assert.IsLessThan(100, stopwatch.ElapsedMilliseconds, "The second call showed a dialog.");
    }

    /// <summary>
    /// <b>Shows the share dialog once per row</b>: share one or more monitors. Each one is captured (the compositor sends a frame when the stream starts),
    /// also with the pointer drawn into the frames.
    /// </summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void SharedMonitorsAreCaptured(bool drawCursor)
    {
        using WaylandScreenCaptureService service = CreateService(drawCursor);
        foreach (Display display in service.GetDisplays(service.GetGraphicsCards().Single()).ToList())
        {
            WaylandScreenCapture capture = service.GetScreenCapture(display);
            CaptureZone<ColorBGRA> zone = capture.RegisterCaptureZone(new CaptureRegion(0, 0, display.Width, display.Height), downscaleLevel: 3);
            HardwareTestHelper.CaptureFrame(capture);

            Assert.AreEqual((display.Width, display.Height), (capture.Display.Width, capture.Display.Height), $"The frames of {display.Id} have another size than the portal told.");

            ColorBGRA[] pixels = HardwareTestHelper.GetPixels(zone);
            Assert.IsTrue(pixels.Any(pixel => (pixel.R | pixel.G | pixel.B) != 0), $"{display.Id} is black.");
            Assert.IsTrue(pixels.All(pixel => pixel.A == byte.MaxValue), $"{display.Id} isn't opaque.");
        }
    }

    /// <summary>
    /// <b>Shows the share dialog</b> unless a stored session is restored: share one or more monitors and let the desktop remember them (KDE: the checkbox).
    /// A second service with the token the first one got starts without the dialog and shares the same monitors.
    /// </summary>
    [TestMethod]
    public void RestoredSessionShowsNoDialog()
    {
        List<Display> displays;
        string? token;
        using (WaylandScreenCaptureService first = CreateService())
        {
            displays = [.. first.GetDisplays(first.GetGraphicsCards().Single())];
            token = first.RestoreToken;
        }

        Assert.IsNotNull(token, "The portal returned no restore token (version 4 needed).");

        using WaylandScreenCaptureService second = CreateService();
        Assert.AreEqual(token, second.RestoreToken);

        Stopwatch stopwatch = Stopwatch.StartNew();
        List<Display> restored = [.. second.GetDisplays(second.GetGraphicsCards().Single())];
        Assert.IsLessThan(1000, stopwatch.ElapsedMilliseconds, "The restored session showed the dialog (or the desktop didn't remember the selection).");
        CollectionAssert.AreEqual(displays, restored);
    }
}
