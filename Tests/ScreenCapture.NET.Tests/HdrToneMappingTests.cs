using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using HPPH;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace ScreenCapture.NET.Tests;

/// <summary>
/// <see cref="HdrToneMapper"/>: the tone mapping of HDR frames, run with the embedded shader on WARP (Direct3D's software rasterizer, available on every Windows, also without a GPU).
/// </summary>
[TestClass]
[OSCondition(OperatingSystems.Windows)]
[SupportedOSPlatform("windows")]
public sealed class HdrToneMappingTests
{
    #region Constants

    // Compile.cmd uses the same flags (fxc /O3 /Qstrip_debug /Qstrip_reflect).
    private const ShaderFlags COMPILE_FLAGS = ShaderFlags.OptimizationLevel3;
    private const StripFlags STRIP_FLAGS = StripFlags.CompilerStripDebugInfo | StripFlags.CompilerStripReflectionData;

    #endregion

    #region Methods

    private static ID3D11Device CreateWarpDevice()
    {
        D3D11.D3D11CreateDevice(null, DriverType.Warp, DeviceCreationFlags.None, [FeatureLevel.Level_10_0], out ID3D11Device? device).CheckError();
        return device!;
    }

    /// <summary>
    /// Tone maps an image of scRGB colors (RGB, row by row) with the given white scales, one after another on the same tone mapper.
    /// </summary>
    private static ColorBGRA[][] ToneMap(float[] rgb, int width, int height, params float[] whiteScales)
    {
        using ID3D11Device device = CreateWarpDevice();
        using ID3D11DeviceContext context = device.ImmediateContext;

        // RGBA, alpha 0: the result is opaque anyway.
        Half[] pixels = new Half[width * height * 4];
        for (int i = 0; i < (width * height); i++)
            for (int channel = 0; channel < 3; channel++)
                pixels[(i * 4) + channel] = (Half)rgb[(i * 3) + channel];

        Texture2DDescription frameDesc = new(Format.R16G16B16A16_Float, (uint)width, (uint)height, 1, 1, BindFlags.ShaderResource);
        using ID3D11Texture2D frame = CreateTexture(device, frameDesc, pixels, width * 8);
        using ID3D11ShaderResourceView frameView = device.CreateShaderResourceView(frame);

        using ID3D11Texture2D target = device.CreateTexture2D(new Texture2DDescription(Format.B8G8R8A8_UNorm, (uint)width, (uint)height, 1, 1, BindFlags.RenderTarget));
        using ID3D11RenderTargetView targetView = device.CreateRenderTargetView(target);
        using ID3D11Texture2D staging = device.CreateTexture2D(new Texture2DDescription(Format.B8G8R8A8_UNorm, (uint)width, (uint)height, 1, 1, BindFlags.None,
                                                                                         ResourceUsage.Staging, CpuAccessFlags.Read));

        using HdrToneMapper toneMapper = new(device);

        ColorBGRA[][] results = new ColorBGRA[whiteScales.Length][];
        for (int i = 0; i < whiteScales.Length; i++)
        {
            toneMapper.Draw(context, frameView, targetView, width, height, whiteScales[i]);
            context.CopyResource(staging, target);

            MappedSubresource mapped = context.Map(staging, 0, MapMode.Read);
            try
            {
                ReadOnlySpan<byte> data = mapped.AsSpan((int)mapped.RowPitch * height);
                results[i] = RefImage<ColorBGRA>.Wrap(data, width, height, (int)mapped.RowPitch).ToArray();
            }
            finally
            {
                context.Unmap(staging, 0);
            }
        }

        return results;
    }

    private static unsafe ID3D11Texture2D CreateTexture<T>(ID3D11Device device, Texture2DDescription description, T[] data, int rowPitch)
        where T : unmanaged
    {
        fixed (T* pointer = data)
            return device.CreateTexture2D(description, new SubresourceData((nint)pointer, (uint)rowPitch));
    }

    internal static float SrgbToLinear(byte value)
    {
        float v = value / 255f;
        return v <= 0.04045f ? v / 12.92f : MathF.Pow((v + 0.055f) / 1.055f, 2.4f);
    }

    private static byte LinearToSrgb(float value)
    {
        float v = value <= 0.0031308f ? value * 12.92f : (1.055f * MathF.Pow(value, 1 / 2.4f)) - 0.055f;
        return (byte)MathF.Round(Math.Clamp(v, 0, 1) * 255);
    }

    // Every value in every channel, in different combinations.
    private static ColorBGRA[] CreateSdrColors()
        => [.. Enumerable.Range(0, 256).Select(i => new ColorBGRA((byte)((i * 7) % 256), (byte)(255 - i), (byte)i, 255))];

    [TestMethod]
    [DataRow(80f)]
    [DataRow(200f)]
    [DataRow(480f)]
    public void SdrColorsKeepTheirValues(float sdrWhiteLevel)
    {
        // How Windows shows SDR content on an HDR display: sRGB decoded and scaled to the SDR white level (1.0 = 80 nits).
        ColorBGRA[] colors = CreateSdrColors();
        float nitsScale = sdrWhiteLevel / HdrToneMapper.SCRGB_WHITE_NITS;
        float[] rgb = [.. colors.SelectMany(c => new[] { SrgbToLinear(c.R) * nitsScale, SrgbToLinear(c.G) * nitsScale, SrgbToLinear(c.B) * nitsScale })];

        ColorBGRA[] result = ToneMap(rgb, 16, 16, HdrToneMapper.GetWhiteScale(sdrWhiteLevel))[0];

        CollectionAssert.AreEqual(colors, result);
    }

    [TestMethod]
    public void EveryPixelIsMappedFromItsPosition()
    {
        // An odd size, so a pixel read from a neighbor or a missing row or column shows.
        const int WIDTH = 7;
        const int HEIGHT = 5;
        ColorBGRA[] colors = [.. Enumerable.Range(0, WIDTH * HEIGHT).Select(i => new ColorBGRA((byte)(i * 3), (byte)(255 - (i * 5)), (byte)(i * 7), 255))];
        float[] rgb = [.. colors.SelectMany(c => new[] { SrgbToLinear(c.R), SrgbToLinear(c.G), SrgbToLinear(c.B) })];

        CollectionAssert.AreEqual(colors, ToneMap(rgb, WIDTH, HEIGHT, 1)[0]);
    }

    [TestMethod]
    public void BrighterColorsKeepTheirHue()
    {
        float[] rgb =
        [
            4, 2, 1,       // Scaled by its brightest channel to (1, 0.5, 0.25), not clipped to (1, 1, 1).
            10, 0, 0,      // A bright red stays red.
            2, 2, 2,       // Bright white is white.
            1, 0.5f, 0.25f // Exactly SDR white in its brightest channel: unchanged.
        ];

        ColorBGRA[] result = ToneMap(rgb, 4, 1, 1)[0];

        ColorBGRA orange = new(LinearToSrgb(0.25f), LinearToSrgb(0.5f), 255, 255);
        CollectionAssert.AreEqual(new[] { orange, new ColorBGRA(0, 0, 255, 255), new ColorBGRA(255, 255, 255, 255), orange }, result);
    }

    [TestMethod]
    public void ColorsOutsideTheGamutAreClamped()
    {
        // Wide gamut colors have negative scRGB values. The clamp happens before the hue is kept (the negative channel doesn't count).
        float[] rgb = [-0.5f, 0.5f, 1, 2, -1, 1];

        ColorBGRA[] result = ToneMap(rgb, 2, 1, 1)[0];

        CollectionAssert.AreEqual(new[] { new ColorBGRA(255, LinearToSrgb(0.5f), 0, 255), new ColorBGRA(LinearToSrgb(0.5f), 0, 255, 255) }, result);
    }

    [TestMethod]
    public void ChangedWhiteScaleIsUsed()
    {
        float[] rgb = [0.5f, 0.5f, 0.5f];

        ColorBGRA[][] results = ToneMap(rgb, 1, 1, 1, 2, 2, 0.5f);

        Assert.AreEqual(LinearToSrgb(0.5f), results[0][0].R);
        Assert.AreEqual(255, results[1][0].R);
        Assert.AreEqual(255, results[2][0].R);
        Assert.AreEqual(LinearToSrgb(0.25f), results[3][0].R);
    }

    [TestMethod]
    public void WhiteScaleMapsSdrWhiteToOne()
    {
        Assert.AreEqual(1f, HdrToneMapper.GetWhiteScale(80));
        Assert.AreEqual(0.4f, HdrToneMapper.GetWhiteScale(200));
        Assert.AreEqual(2f, HdrToneMapper.GetWhiteScale(40));

        // Unusable values don't scale.
        foreach (float invalid in new[] { 0f, -80f, float.NaN, float.PositiveInfinity })
            Assert.AreEqual(1f, HdrToneMapper.GetWhiteScale(invalid), $"{invalid}");
    }

    [TestMethod]
    [DataRow("VSMain", "vs_4_0", "HdrToneMapping.vs.cso")]
    [DataRow("PSMain", "ps_4_0", "HdrToneMapping.ps.cso")]
    public void EmbeddedBytecodeMatchesTheSource(string entryPoint, string profile, string resource)
    {
        // Fails if HdrToneMapping.hlsl was changed without running Compile.cmd.
        string source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Shaders", "HdrToneMapping.hlsl"));
        ReadOnlyMemory<byte> compiled = Compiler.Compile(source, entryPoint, "HdrToneMapping.hlsl", profile, COMPILE_FLAGS);
        byte[] stripped = Strip(compiled.Span);

        using Stream stream = typeof(HdrToneMapper).Assembly.GetManifestResourceStream($"ScreenCapture.NET.Shaders.{resource}")!;
        Assert.IsNotNull(stream, resource);
        byte[] embedded = new byte[stream.Length];
        stream.ReadExactly(embedded);

        CollectionAssert.AreEqual(stripped, embedded, "The embedded shader doesn't match HdrToneMapping.hlsl: run Shared/D3D11/Shaders/Compile.cmd.");
    }

    private static unsafe byte[] Strip(ReadOnlySpan<byte> bytecode)
    {
        fixed (byte* pointer = bytecode)
        {
            Compiler.StripShader((nint)pointer, (nuint)bytecode.Length, STRIP_FLAGS, out Blob stripped).CheckError();
            using (stripped)
                return stripped.AsBytes();
        }
    }

    #endregion
}
