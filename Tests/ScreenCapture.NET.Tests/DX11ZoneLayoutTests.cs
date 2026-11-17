using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using HPPH;

namespace ScreenCapture.NET.Tests;

/// <summary>
/// Checks how DX11 maps a zone (display orientation) to the duplicated texture (native orientation).
/// </summary>
[TestClass]
[OSCondition(OperatingSystems.Windows)]
[SupportedOSPlatform("windows")]
public sealed class DX11ZoneLayoutTests
{
    #region Constants

    // A zone at (10, 20) with size 300x400 on a 1920x1080 display.
    private const int DISPLAY_WIDTH = 1920;
    private const int DISPLAY_HEIGHT = 1080;

    #endregion

    #region Methods

    [TestMethod]
    [DataRow(ImageRotation.None, 10, 20, 300, 400)]
    [DataRow(ImageRotation.Rotate90, 20, 1920 - 10 - 300, 400, 300)]
    [DataRow(ImageRotation.Rotate180, 1920 - 10 - 300, 1080 - 20 - 400, 300, 400)]
    [DataRow(ImageRotation.Rotate270, 1080 - 20 - 400, 10, 400, 300)]
    public void SourceBox(ImageRotation rotation, int expectedX, int expectedY, int expectedWidth, int expectedHeight)
        => Assert.AreEqual(new SourceBox(expectedX, expectedY, expectedWidth, expectedHeight),
                           DX11ZoneLayout.GetSourceBox(rotation, DISPLAY_WIDTH, DISPLAY_HEIGHT, 10, 20, 300, 400));

    [TestMethod]
    [DataRow(ImageRotation.None)]
    [DataRow(ImageRotation.Rotate90)]
    [DataRow(ImageRotation.Rotate180)]
    [DataRow(ImageRotation.Rotate270)]
    public void FullScreenSourceBoxCoversTexture(ImageRotation rotation)
    {
        (int textureWidth, int textureHeight) = DX11ZoneLayout.GetTextureSize(rotation, DISPLAY_WIDTH, DISPLAY_HEIGHT);

        Assert.AreEqual(new SourceBox(0, 0, textureWidth, textureHeight),
                        DX11ZoneLayout.GetSourceBox(rotation, DISPLAY_WIDTH, DISPLAY_HEIGHT, 0, 0, DISPLAY_WIDTH, DISPLAY_HEIGHT));
    }

    [TestMethod]
    [DataRow(ImageRotation.None, 300, 400)]
    [DataRow(ImageRotation.Rotate90, 400, 300)]
    [DataRow(ImageRotation.Rotate180, 300, 400)]
    [DataRow(ImageRotation.Rotate270, 400, 300)]
    public void TextureSize(ImageRotation rotation, int expectedWidth, int expectedHeight)
        => Assert.AreEqual((expectedWidth, expectedHeight), DX11ZoneLayout.GetTextureSize(rotation, 300, 400));

    [TestMethod]
    public void SourceBoxConvertsToExclusiveBox()
    {
        Vortice.Mathematics.Box box = new SourceBox(10, 20, 300, 400).ToBox();

        Assert.AreEqual((10, 20, 0, 310, 420, 1), (box.Left, box.Top, box.Front, box.Right, box.Bottom, box.Back));
    }

    /// <summary>
    /// Simulates the DX11 pipeline (copy the source box out of the texture, then <see cref="CopyRotateHelper"/>)
    /// and checks that a partial zone shows the same pixels as the same area of a full-screen zone.
    /// </summary>
    [TestMethod]
    [DataRow(ImageRotation.None)]
    [DataRow(ImageRotation.Rotate90)]
    [DataRow(ImageRotation.Rotate180)]
    [DataRow(ImageRotation.Rotate270)]
    public void PartialZoneMatchesFullScreen(ImageRotation rotation)
    {
        const int displayWidth = 13;
        const int displayHeight = 7;

        (int textureWidth, int textureHeight) = DX11ZoneLayout.GetTextureSize(rotation, displayWidth, displayHeight);
        ColorBGRA[] texture = new ColorBGRA[textureWidth * textureHeight];
        for (int i = 0; i < texture.Length; i++)
            texture[i] = new ColorBGRA((byte)(i % textureWidth), (byte)(i / textureWidth), 0x42, 0xFF);

        ColorBGRA[] fullScreen = Capture(rotation, texture, textureWidth, displayWidth, displayHeight, 0, 0, displayWidth, displayHeight);

        for (int y = 0; y < displayHeight; y++)
            for (int x = 0; x < displayWidth; x++)
                for (int height = 1; (y + height) <= displayHeight; height++)
                    for (int width = 1; (x + width) <= displayWidth; width++)
                    {
                        ColorBGRA[] zone = Capture(rotation, texture, textureWidth, displayWidth, displayHeight, x, y, width, height);

                        for (int zoneY = 0; zoneY < height; zoneY++)
                            for (int zoneX = 0; zoneX < width; zoneX++)
                                if (!zone[(zoneY * width) + zoneX].Equals(fullScreen[((y + zoneY) * displayWidth) + x + zoneX]))
                                    Assert.Fail($"Zone ({x}, {y}, {width}, {height}) differs from the full screen at ({zoneX}, {zoneY}).");
                    }
    }

    private static ColorBGRA[] Capture(ImageRotation rotation, ColorBGRA[] texture, int textureWidth, int displayWidth, int displayHeight, int x, int y, int width, int height)
    {
        SourceBox box = DX11ZoneLayout.GetSourceBox(rotation, displayWidth, displayHeight, x, y, width, height);

        // The staging texture: the source box with some row padding, like a mapped texture.
        int stride = (box.Width + 3) * 4;
        byte[] staging = new byte[stride * box.Height];
        for (int row = 0; row < box.Height; row++)
            MemoryMarshal.AsBytes(texture.AsSpan(((box.Y + row) * textureWidth) + box.X, box.Width)).CopyTo(staging.AsSpan(row * stride));

        byte[] target = new byte[width * height * 4];
        CopyRotateHelper.Copy(rotation, staging, stride, width, height, target);

        return MemoryMarshal.Cast<byte, ColorBGRA>(target).ToArray();
    }

    #endregion
}
