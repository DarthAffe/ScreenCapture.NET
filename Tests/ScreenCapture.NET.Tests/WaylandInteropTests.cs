extern alias Wayland;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Wayland::ScreenCapture.NET;
using static Wayland::ScreenCapture.NET.PipeWire;

namespace ScreenCapture.NET.Tests;

/// <summary>
/// Checks the parts of the Wayland backend that need no session bus.
/// </summary>
[TestClass]
[SupportedOSPlatform("linux")] // The Wayland assembly is marked linux-only, but these members are pure managed code that also runs on Windows.
public sealed class WaylandInteropTests
{
    // The PODs as libspa's builder makes them (printed by SpaPodReference.c).
    private const string ENUM_FORMAT_1920X1080 =
        "D80000000F000000030004000300000001000000000000000400000003000000" +
        "0200000000000000020000000000000004000000030000000100000000000000" +
        "01000200000000001C0000001300000003000000000000000400000003000000" +
        "08000000080000000C0000000000000003000200000000002800000013000000" +
        "0100000000000000080000000A00000080070000380400000100000001000000" +
        "0040000000400000040002000000000028000000130000000100000000000000" +
        "080000000B00000000000000010000000000000001000000E803000001000000";
    private const string BUFFERS =
        "600000000F000000040004000500000001000000000000001C00000013000000" +
        "010000000000000004000000040000000300000002000000FFFFFF7F00000000" +
        "0600000000000000140000001300000004000000000000000400000004000000" +
        "0600000000000000";
    private const string META_HEADER =
        "380000000F000000050004000600000001000000000000000400000003000000" +
        "0100000000000000020000000000000004000000040000002000000000000000";
    private const string FORMAT_BGRA_2560X1440 =
        "B80000000F000000030004000400000001000000000000000400000003000000" +
        "0200000000000000020000000000000004000000030000000100000000000000" +
        "010002000000000004000000030000000C000000000000000500020000000000" +
        "28000000130000000100000000000000080000000B0000003C00000001000000" +
        "01000000010000003C000000010000000300020000000000080000000A000000" +
        "000A0000A00500000400020000000000080000000B0000000000000001000000";
    private const string FORMAT_CHOICES_BGRX_800X600 =
        "980000000F000000030004000400000001000000000000000400000003000000" +
        "0200000000000000020000000000000004000000030000000100000000000000" +
        "0100020000000000180000001300000003000000000000000400000003000000" +
        "080000000C000000030002000000000028000000130000000100000000000000" +
        "080000000A000000200300005802000001000000010000000010000000100000";
    private const string FORMAT_MJPG =
        "500000000F000000030004000400000001000000000000000400000003000000" +
        "0200000000000000020000000000000004000000030000000200020000000000" +
        "0300020000000000080000000A00000080020000E0010000";

    [TestMethod]
    [DataRow(":1.42", "/org/freedesktop/portal/desktop/request/1_42/token_1")]
    [DataRow(":1.1234.5", "/org/freedesktop/portal/desktop/request/1_1234_5/token_1")]
    public void RequestPathIsUnderTheEscapedUniqueName(string uniqueName, string path)
        => Assert.AreEqual(path, ScreenCastSession.GetRequestPath(uniqueName, "token_1"));

    [TestMethod]
    public void CancelledResponseSaysSo()
    {
        Assert.Contains("cancelled", ScreenCastSession.CreateResponseException("Start", 1).Message);
        Assert.DoesNotContain("cancelled", ScreenCastSession.CreateResponseException("Start", 2).Message);
    }

    [TestMethod]
    [DataRow("KDE", "KDE")]
    [DataRow("ubuntu:GNOME", "ubuntu:GNOME")]
    [DataRow("", "xdg-desktop-portal")]
    [DataRow(null, "xdg-desktop-portal")]
    public void GraphicsCardIsNamedAfterTheDesktop(string? desktop, string name)
    {
        string? previous = Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP");
        try
        {
            Environment.SetEnvironmentVariable("XDG_CURRENT_DESKTOP", desktop);

            Assert.AreEqual(new GraphicsCard("xdg-desktop-portal", 0, name, 0, 0), WaylandDisplays.GetGraphicsCard());
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_CURRENT_DESKTOP", previous);
        }
    }

    [TestMethod]
    public void EnumFormatIsLibspas() => Assert.AreEqual(ENUM_FORMAT_1920X1080, Convert.ToHexString(SpaPod.BuildEnumFormat(1920, 1080)));

    [TestMethod]
    public void BuffersAreLibspas() => Assert.AreEqual(BUFFERS, Convert.ToHexString(SpaPod.BuildBuffers()));

    [TestMethod]
    public void MetaHeaderIsLibspas() => Assert.AreEqual(META_HEADER, Convert.ToHexString(SpaPod.BuildMetaHeader()));

    [TestMethod]
    public void EnumFormatHasTheSizeAsDefault()
    {
        byte[] pod = SpaPod.BuildEnumFormat(800, 600);

        Assert.IsTrue(SpaPod.TryReadVideoFormat(pod, out uint videoFormat, out int width, out int height));
        Assert.AreEqual((SPA_VIDEO_FORMAT_BGRx, 800, 600), (videoFormat, width, height));
    }

    /// <summary>
    /// A display without size (the portal didn't tell) still offers a valid range.
    /// </summary>
    [TestMethod]
    public void EnumFormatWithoutSizeDefaultsToOnePixel()
    {
        Assert.IsTrue(SpaPod.TryReadVideoFormat(SpaPod.BuildEnumFormat(0, 0), out _, out int width, out int height));
        Assert.AreEqual((1, 1), (width, height));
    }

    [TestMethod]
    [DataRow(FORMAT_BGRA_2560X1440, SPA_VIDEO_FORMAT_BGRA, 2560, 1440)]
    [DataRow(FORMAT_CHOICES_BGRX_800X600, SPA_VIDEO_FORMAT_BGRx, 800, 600)]
    public void FormatIsRead(string pod, uint expectedFormat, int expectedWidth, int expectedHeight)
    {
        Assert.IsTrue(SpaPod.TryReadVideoFormat(Convert.FromHexString(pod), out uint videoFormat, out int width, out int height));
        Assert.AreEqual((expectedFormat, expectedWidth, expectedHeight), (videoFormat, width, height));
    }

    [TestMethod]
    public void OtherFormatsAreNoVideoFormat()
    {
        Assert.IsFalse(SpaPod.TryReadVideoFormat(Convert.FromHexString(FORMAT_MJPG), out _, out _, out _), "mjpg");
        Assert.IsFalse(SpaPod.TryReadVideoFormat(SpaPod.BuildBuffers(), out _, out _, out _), "no format object");
    }

    [TestMethod]
    public void TruncatedFormatIsNoVideoFormat()
    {
        byte[] pod = Convert.FromHexString(FORMAT_BGRA_2560X1440);

        for (int length = 0; length < pod.Length; length += 4)
            Assert.IsFalse(SpaPod.TryReadVideoFormat(pod.AsSpan(0, length), out _, out _, out _), $"length {length}");

        // A property that claims more than the object has.
        byte[] oversized = Convert.FromHexString(FORMAT_BGRA_2560X1440);
        oversized[^16] = 0xFF;
        Assert.IsFalse(SpaPod.TryReadVideoFormat(oversized, out _, out _, out _), "property size");

        // A header that claims more than there is.
        pod[0]++;
        Assert.IsFalse(SpaPod.TryReadVideoFormat(pod.AsSpan(0, pod.Length - 1), out _, out _, out _), "size");
    }

    /// <summary>
    /// Pins the layouts to the ones of libpipewire 1.4 on 64-bit Linux (printed with offsetof/sizeof by a C program).
    /// </summary>
    [TestMethod]
    [OSCondition(OperatingSystems.Linux)]
    public unsafe void PipeWireLayoutsMatchOn64BitLinux()
    {
        if (!Environment.Is64BitProcess)
            Assert.Inconclusive("The layouts are pinned for 64-bit processes.");

        Assert.AreEqual(96, sizeof(PwStreamEvents));
        Assert.AreEqual(16, (int)Marshal.OffsetOf<PwStreamEvents>(nameof(PwStreamEvents.state_changed)));
        Assert.AreEqual(40, (int)Marshal.OffsetOf<PwStreamEvents>(nameof(PwStreamEvents.param_changed)));
        Assert.AreEqual(56, (int)Marshal.OffsetOf<PwStreamEvents>(nameof(PwStreamEvents.remove_buffer)));
        Assert.AreEqual(64, (int)Marshal.OffsetOf<PwStreamEvents>(nameof(PwStreamEvents.process)));

        Assert.AreEqual(40, sizeof(PwBuffer));
        Assert.AreEqual(24, sizeof(SpaBuffer));
        Assert.AreEqual(8, (int)Marshal.OffsetOf<SpaBuffer>(nameof(SpaBuffer.metas)));
        Assert.AreEqual(16, (int)Marshal.OffsetOf<SpaBuffer>(nameof(SpaBuffer.datas)));

        Assert.AreEqual(40, sizeof(SpaData));
        Assert.AreEqual(20, (int)Marshal.OffsetOf<SpaData>(nameof(SpaData.maxsize)));
        Assert.AreEqual(24, (int)Marshal.OffsetOf<SpaData>(nameof(SpaData.data)));
        Assert.AreEqual(32, (int)Marshal.OffsetOf<SpaData>(nameof(SpaData.chunk)));

        Assert.AreEqual(16, sizeof(SpaChunk));
        Assert.AreEqual(8, (int)Marshal.OffsetOf<SpaChunk>(nameof(SpaChunk.stride)));
        Assert.AreEqual(16, sizeof(SpaMeta));
        Assert.AreEqual(8, (int)Marshal.OffsetOf<SpaMeta>(nameof(SpaMeta.data)));
        Assert.AreEqual(32, sizeof(SpaMetaHeader));
        Assert.AreEqual(8, (int)Marshal.OffsetOf<SpaMetaHeader>(nameof(SpaMetaHeader.pts)));
    }

    [TestMethod]
    public void PresentationTimeIsAStopwatchTimestamp()
    {
        Assert.AreEqual(0, PipeWireStream.ToStopwatchTimestamp(0), "none");
        Assert.AreEqual(0, PipeWireStream.ToStopwatchTimestamp(-5), "none");
        Assert.AreEqual(Stopwatch.Frequency * 3, PipeWireStream.ToStopwatchTimestamp(3_000_000_000));

        // Days of uptime in nanoseconds don't overflow.
        long tenDays = 10L * 24 * 3600 * 1_000_000_000;
        Assert.AreEqual(Stopwatch.Frequency * 10 * 24 * 3600, PipeWireStream.ToStopwatchTimestamp(tenDays));
    }

    [TestMethod]
    public void WaylandOptionsHaveATimeoutOfOneSecond()
    {
        Assert.AreEqual(TimeSpan.FromSeconds(1), WaylandCaptureOptions.Default.Timeout);
        Assert.AreEqual(WaylandCaptureOptions.Default, new WaylandCaptureOptions());
        Assert.AreNotEqual(WaylandCaptureOptions.Default, new WaylandCaptureOptions { Timeout = TimeSpan.Zero });

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new WaylandCaptureOptions { Timeout = TimeSpan.FromMilliseconds(-1) });
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new WaylandCaptureOptions { Timeout = WaylandCaptureOptions.MaxTimeout + TimeSpan.FromMilliseconds(1) });
    }
}
