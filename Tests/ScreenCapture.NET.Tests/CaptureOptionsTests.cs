using System.Runtime.Versioning;

namespace ScreenCapture.NET.Tests;

/// <summary>
/// Checks the option values (no capture needed). The DX11 and DX9 assemblies are Windows-only.
/// </summary>
[TestClass]
[OSCondition(OperatingSystems.Windows)]
[SupportedOSPlatform("windows")]
public sealed class CaptureOptionsTests
{
    [TestMethod]
    public void DX11Defaults()
    {
        DX11CaptureOptions options = new();

        Assert.AreEqual(TimeSpan.FromSeconds(1), options.Timeout);
        Assert.IsFalse(options.UseDuplicateOutput1);
        Assert.IsFalse(options.DrawCursor);
        Assert.AreEqual(DX11CaptureOptions.Default, options);
    }

    [TestMethod]
    public void DX11TimeoutBoundsAreAccepted()
    {
        Assert.AreEqual(TimeSpan.Zero, new DX11CaptureOptions { Timeout = TimeSpan.Zero }.Timeout);
        Assert.AreEqual(DX11CaptureOptions.MaxTimeout, new DX11CaptureOptions { Timeout = DX11CaptureOptions.MaxTimeout }.Timeout);
    }

    [TestMethod]
    public void DX11TimeoutOutOfRangeThrows()
    {
        TimeSpan[] invalid = [TimeSpan.FromTicks(-1), Timeout.InfiniteTimeSpan, DX11CaptureOptions.MaxTimeout + TimeSpan.FromTicks(1), TimeSpan.MaxValue];

        foreach (TimeSpan timeout in invalid)
        {
            ArgumentOutOfRangeException exception = Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new DX11CaptureOptions { Timeout = timeout }, $"{timeout}");
            Assert.AreEqual(nameof(DX11CaptureOptions.Timeout), exception.ParamName);

            // 'with' validates too.
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => DX11CaptureOptions.Default with { Timeout = timeout }, $"{timeout} (with)");
        }
    }

    [TestMethod]
    public void DX11TimeoutIsRoundedUpToMilliseconds()
    {
        Assert.AreEqual(0, DX11CaptureOptions.ToMilliseconds(TimeSpan.Zero));
        Assert.AreEqual(1, DX11CaptureOptions.ToMilliseconds(TimeSpan.FromTicks(1)));
        Assert.AreEqual(1, DX11CaptureOptions.ToMilliseconds(TimeSpan.FromMilliseconds(1)));
        Assert.AreEqual(2, DX11CaptureOptions.ToMilliseconds(TimeSpan.FromMilliseconds(1.5)));
        Assert.AreEqual(1000, DX11CaptureOptions.ToMilliseconds(TimeSpan.FromSeconds(1)));
        Assert.AreEqual(int.MaxValue, DX11CaptureOptions.ToMilliseconds(DX11CaptureOptions.MaxTimeout));
    }

    [TestMethod]
    public void DX11OptionsAreEqualByValue()
    {
        DX11CaptureOptions options = new() { Timeout = TimeSpan.FromMilliseconds(250), UseDuplicateOutput1 = true };

        Assert.AreEqual(options, new DX11CaptureOptions { Timeout = TimeSpan.FromMilliseconds(250), UseDuplicateOutput1 = true });
        Assert.AreNotEqual(options, options with { Timeout = TimeSpan.FromMilliseconds(251) });
        Assert.AreNotEqual(options, options with { UseDuplicateOutput1 = false });
        Assert.AreNotEqual(options, options with { DrawCursor = true });
    }

    [TestMethod]
    public void DX9OptionsWithoutValuesAreAllEqual()
    {
        // X11CaptureOptions (Linux-only assembly) is checked by X11HardwareTests.OptionsAreUsedForCaptures.
        Assert.AreEqual(DX9CaptureOptions.Default, new DX9CaptureOptions());
    }
}
