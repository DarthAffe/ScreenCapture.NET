using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using HPPH;

namespace ScreenCapture.NET.Tests;

/// <summary>
/// Checks the X11 interop types without an X server.
/// </summary>
[TestClass]
[SupportedOSPlatform("linux")] // The X11 assembly is marked linux-only, but these members are pure managed code that also runs on Windows.
public sealed class X11InteropTests
{
    [TestMethod]
    public void BGRAFormatIsSupported()
        => Assert.IsTrue(X11ImageHandle.IsBGRAFormat(CreateImage()));

    [TestMethod]
    [DataRow(24)]
    [DataRow(16)]
    public void OtherBitsPerPixelAreNotSupported(int bitsPerPixel)
        => Assert.IsFalse(X11ImageHandle.IsBGRAFormat(CreateImage(bitsPerPixel: bitsPerPixel)));

    [TestMethod]
    public void MSBFirstIsNotSupported()
        => Assert.IsFalse(X11ImageHandle.IsBGRAFormat(CreateImage(byteOrder: 1)));

    [TestMethod]
    public void RGBMasksAreNotSupported()
        => Assert.IsFalse(X11ImageHandle.IsBGRAFormat(CreateImage(redMask: 0xFF, blueMask: 0xFF0000)));

    [TestMethod]
    public void TenBitChannelsAreNotSupported()
        => Assert.IsFalse(X11ImageHandle.IsBGRAFormat(CreateImage(redMask: 0x3FF00000, greenMask: 0xFFC00, blueMask: 0x3FF)));

    /// <summary>
    /// Covers the vectorized blocks and the remainder for every length up to three blocks.
    /// </summary>
    [TestMethod]
    public void SetOpaqueOnlyChangesTheAlpha()
    {
        Random random = new(42);

        for (int length = 0; length <= ((3 * Vector<uint>.Count) + 1); length++)
        {
            ColorBGRA[] pixels = new ColorBGRA[length];
            for (int i = 0; i < length; i++)
                pixels[i] = new ColorBGRA((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(255));

            ColorBGRA[] expected = [.. pixels.Select(p => new ColorBGRA(p.B, p.G, p.R, byte.MaxValue))];

            X11ScreenCapture.SetOpaque(pixels);

            CollectionAssert.AreEqual(expected, pixels, $"length {length}");
        }
    }

    /// <summary>
    /// Pins the layout of <see cref="X11.XImage"/> to Xlib's on 64-bit Linux, where <c>unsigned long</c> is 8 bytes.
    /// With 4-byte masks every field from <c>red_mask</c> on is read from the wrong offset.
    /// </summary>
    [TestMethod]
    [OSCondition(OperatingSystems.Linux)]
    public void XImageMatchesXlibLayoutOn64BitLinux()
    {
        if (!Environment.Is64BitProcess)
            Assert.Inconclusive("The layout is pinned for 64-bit processes.");

        Assert.AreEqual(16, (int)Marshal.OffsetOf<X11.XImage>(nameof(X11.XImage.data)));
        Assert.AreEqual(44, (int)Marshal.OffsetOf<X11.XImage>(nameof(X11.XImage.bytes_per_line)));
        Assert.AreEqual(48, (int)Marshal.OffsetOf<X11.XImage>(nameof(X11.XImage.bits_per_pixel)));
        Assert.AreEqual(56, (int)Marshal.OffsetOf<X11.XImage>(nameof(X11.XImage.red_mask)));
        Assert.AreEqual(64, (int)Marshal.OffsetOf<X11.XImage>(nameof(X11.XImage.green_mask)));
        Assert.AreEqual(72, (int)Marshal.OffsetOf<X11.XImage>(nameof(X11.XImage.blue_mask)));
        Assert.AreEqual(80, (int)Marshal.OffsetOf<X11.XImage>(nameof(X11.XImage.obdata)));
    }

    /// <summary>
    /// Pins the layout of <see cref="X11.XShmSegmentInfo"/> to libXext's on 64-bit Linux (<c>ShmSeg</c> is an <c>unsigned long</c>).
    /// </summary>
    [TestMethod]
    [OSCondition(OperatingSystems.Linux)]
    public unsafe void XShmSegmentInfoMatchesXextLayoutOn64BitLinux()
    {
        if (!Environment.Is64BitProcess)
            Assert.Inconclusive("The layout is pinned for 64-bit processes.");

        Assert.AreEqual(8, (int)Marshal.OffsetOf<X11.XShmSegmentInfo>(nameof(X11.XShmSegmentInfo.shmid)));
        Assert.AreEqual(16, (int)Marshal.OffsetOf<X11.XShmSegmentInfo>(nameof(X11.XShmSegmentInfo.shmaddr)));
        Assert.AreEqual(24, (int)Marshal.OffsetOf<X11.XShmSegmentInfo>(nameof(X11.XShmSegmentInfo.readOnly)));
        Assert.AreEqual(32, sizeof(X11.XShmSegmentInfo));
    }

    /// <summary>
    /// Pins the layout of <see cref="X11.XRRMonitorInfo"/> to libXrandr's on 64-bit Linux (<c>Atom</c> is an <c>unsigned long</c>, <c>outputs</c> is aligned to 8 bytes).
    /// </summary>
    [TestMethod]
    [OSCondition(OperatingSystems.Linux)]
    public unsafe void XRRMonitorInfoMatchesXrandrLayoutOn64BitLinux()
    {
        if (!Environment.Is64BitProcess)
            Assert.Inconclusive("The layout is pinned for 64-bit processes.");

        Assert.AreEqual(8, (int)Marshal.OffsetOf<X11.XRRMonitorInfo>(nameof(X11.XRRMonitorInfo.primary)));
        Assert.AreEqual(16, (int)Marshal.OffsetOf<X11.XRRMonitorInfo>(nameof(X11.XRRMonitorInfo.noutput)));
        Assert.AreEqual(20, (int)Marshal.OffsetOf<X11.XRRMonitorInfo>(nameof(X11.XRRMonitorInfo.x)));
        Assert.AreEqual(32, (int)Marshal.OffsetOf<X11.XRRMonitorInfo>(nameof(X11.XRRMonitorInfo.height)));
        Assert.AreEqual(48, (int)Marshal.OffsetOf<X11.XRRMonitorInfo>(nameof(X11.XRRMonitorInfo.outputs)));
        Assert.AreEqual(56, sizeof(X11.XRRMonitorInfo));
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(3)]
    public void MonitorNameIsReadFromItsDescriptor(int descriptor)
    {
        byte[] edid = CreateEdid();
        SetDescriptor(edid, descriptor, 0xFC, "DELL U2720Q\n ");

        Assert.AreEqual("DELL U2720Q", X11Displays.GetMonitorName(edid));
    }

    /// <summary>
    /// A name of 13 characters has no line feed; some monitors also pad shorter names with spaces only.
    /// </summary>
    [TestMethod]
    [DataRow("ABCDEFGHIJKLM", "ABCDEFGHIJKLM")]
    [DataRow("SHORT        ", "SHORT")]
    public void MonitorNameWithoutLineFeedIsTrimmed(string text, string name)
    {
        byte[] edid = CreateEdid();
        SetDescriptor(edid, 1, 0xFC, text);

        Assert.AreEqual(name, X11Displays.GetMonitorName(edid));
    }

    [TestMethod]
    public void OtherDescriptorsAreNoMonitorName()
    {
        byte[] edid = CreateEdid();
        SetDescriptor(edid, 1, 0xFF, "SERIAL123\n   "); // serial number
        SetDescriptor(edid, 2, 0xFE, "TEXT\n        "); // unspecified text

        // A detailed timing that happens to have 0xFC at the tag position.
        SetDescriptor(edid, 3, 0xFC, "TIMING\n      ");
        edid[54 + (3 * 18)] = 0x01;

        Assert.AreEqual(string.Empty, X11Displays.GetMonitorName(edid));
    }

    [TestMethod]
    public void InvalidEdidsHaveNoMonitorName()
    {
        byte[] edid = CreateEdid();
        SetDescriptor(edid, 1, 0xFC, "NAME\n        ");
        Assert.AreEqual("NAME", X11Displays.GetMonitorName(edid));

        Assert.AreEqual(string.Empty, X11Displays.GetMonitorName(edid.AsSpan(0, 127)), "too short");
        Assert.AreEqual(string.Empty, X11Displays.GetMonitorName([]), "empty");

        edid[7] = 0x01;
        Assert.AreEqual(string.Empty, X11Displays.GetMonitorName(edid), "wrong header");
    }

    // The base block with its header; every descriptor is a detailed timing (pixel clock != 0).
    private static byte[] CreateEdid()
    {
        byte[] edid = new byte[128];
        ((ReadOnlySpan<byte>)[0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x00]).CopyTo(edid);
        for (int i = 0; i < 4; i++)
            edid[54 + (i * 18)] = 0x01;

        return edid;
    }

    private static void SetDescriptor(byte[] edid, int index, byte tag, string text)
    {
        Span<byte> descriptor = edid.AsSpan(54 + (index * 18), 18);
        descriptor.Clear();
        descriptor[3] = tag;
        System.Text.Encoding.ASCII.GetBytes(text, descriptor[5..]);
    }

    [TestMethod]
    public void X11OptionsUseSharedMemoryByDefault()
    {
        Assert.IsTrue(X11CaptureOptions.Default.UseSharedMemory);
        Assert.AreEqual(X11CaptureOptions.Default, new X11CaptureOptions());
        Assert.AreNotEqual(X11CaptureOptions.Default, new X11CaptureOptions { UseSharedMemory = false });
    }

    private static X11.XImage CreateImage(int bitsPerPixel = 32, int byteOrder = X11.LSB_FIRST, nuint redMask = 0xFF0000, nuint greenMask = 0xFF00, nuint blueMask = 0xFF)
        => new()
        {
            bits_per_pixel = bitsPerPixel,
            byte_order = byteOrder,
            red_mask = new CULong(redMask),
            green_mask = new CULong(greenMask),
            blue_mask = new CULong(blueMask)
        };
}
