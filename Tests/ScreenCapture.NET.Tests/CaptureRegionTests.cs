namespace ScreenCapture.NET.Tests;

[TestClass]
public sealed class CaptureRegionTests
{
    [TestMethod]
    [DataRow(0, 0, 1920, 1080, true)]
    [DataRow(1919, 1079, 1, 1, true)]
    [DataRow(100, 200, 300, 400, true)]
    [DataRow(1, 0, 1920, 1080, false)]
    [DataRow(0, 1, 1920, 1080, false)]
    [DataRow(1920, 0, 1, 1, false)]
    [DataRow(0, 1080, 1, 1, false)]
    [DataRow(-1, 0, 1, 1, false)]
    [DataRow(0, -1, 1, 1, false)]
    [DataRow(0, 0, 0, 1, false)]
    [DataRow(0, 0, 1, 0, false)]
    [DataRow(0, 0, -1, 1, false)]
    [DataRow(int.MaxValue, 0, int.MaxValue, 1, false)]
    [DataRow(0, 0, int.MaxValue, 1, false)]
    public void IsWithinChecksTheBounds(int x, int y, int width, int height, bool expected)
        => Assert.AreEqual(expected, new CaptureRegion(x, y, width, height).IsWithin(1920, 1080));
}
