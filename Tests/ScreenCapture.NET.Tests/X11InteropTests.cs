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
