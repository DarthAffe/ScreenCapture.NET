using System.Runtime.Versioning;

namespace ScreenCapture.NET.Tests;

/// <summary>
/// Pins down how DX11 maps a zone (display orientation) to the duplicated texture (native orientation).
/// The mirroring of partial zones on rotated displays isn't implemented yet; these tests describe the current axis swap.
/// </summary>
[TestClass]
[OSCondition(OperatingSystems.Windows)]
[SupportedOSPlatform("windows")]
public sealed class DX11ZoneLayoutTests
{
    [TestMethod]
    [DataRow(Rotation.None)]
    [DataRow(Rotation.Rotate180)]
    public void SourceBoxKeepsAxes(Rotation rotation)
        => Assert.AreEqual(new SourceBox(10, 20, 300, 400), DX11ZoneLayout.GetSourceBox(rotation, 10, 20, 300, 400));

    [TestMethod]
    [DataRow(Rotation.Rotate90)]
    [DataRow(Rotation.Rotate270)]
    public void SourceBoxSwapsAxes(Rotation rotation)
        => Assert.AreEqual(new SourceBox(20, 10, 400, 300), DX11ZoneLayout.GetSourceBox(rotation, 10, 20, 300, 400));

    [TestMethod]
    [DataRow(Rotation.None, 300, 400)]
    [DataRow(Rotation.Rotate90, 400, 300)]
    [DataRow(Rotation.Rotate180, 300, 400)]
    [DataRow(Rotation.Rotate270, 400, 300)]
    public void TextureSize(Rotation rotation, int expectedWidth, int expectedHeight)
        => Assert.AreEqual((expectedWidth, expectedHeight), DX11ZoneLayout.GetTextureSize(rotation, 300, 400));

    [TestMethod]
    public void SourceBoxConvertsToExclusiveBox()
    {
        Vortice.Mathematics.Box box = new SourceBox(10, 20, 300, 400).ToBox();

        Assert.AreEqual((10, 20, 0, 310, 420, 1), (box.Left, box.Top, box.Front, box.Right, box.Bottom, box.Back));
    }
}
