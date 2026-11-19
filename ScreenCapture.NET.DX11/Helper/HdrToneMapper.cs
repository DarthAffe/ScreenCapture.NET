using System;
using System.IO;
using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;

namespace ScreenCapture.NET;

/// <summary>
/// Tone maps HDR frames (scRGB, <c>R16G16B16A16_FLOAT</c>) into 8-bit sRGB textures on the GPU (<c>Shaders/HdrToneMapping.hlsl</c>).
/// </summary>
/// <remarks>
/// Colors up to SDR white keep the value they have on an SDR display; brighter ones are scaled down as a whole (keeping their hue), colors outside the sRGB gamut are clamped to it.
/// Not thread-safe: used with the immediate context of its device under the capture lock.
/// </remarks>
internal sealed class HdrToneMapper : IDisposable
{
    #region Constants

    /// <summary>
    /// The luminance of 1.0 in scRGB, in nits.
    /// </summary>
    public const float SCRGB_WHITE_NITS = 80;

    private const string VERTEX_SHADER_RESOURCE = "ScreenCapture.NET.Shaders.HdrToneMapping.vs.cso";
    private const string PIXEL_SHADER_RESOURCE = "ScreenCapture.NET.Shaders.HdrToneMapping.ps.cso";

    private static readonly Lazy<byte[]> VERTEX_SHADER = new(() => ReadShader(VERTEX_SHADER_RESOURCE));
    private static readonly Lazy<byte[]> PIXEL_SHADER = new(() => ReadShader(PIXEL_SHADER_RESOURCE));

    #endregion

    #region Properties & Fields

    private readonly ID3D11VertexShader _vertexShader;
    private readonly ID3D11PixelShader _pixelShader;
    private readonly ID3D11Buffer _constants;

    // What _constants holds (NaN: nothing yet).
    private float _whiteScale = float.NaN;

    #endregion

    #region Constructors

    public HdrToneMapper(ID3D11Device device)
    {
        try
        {
            _vertexShader = device.CreateVertexShader(VERTEX_SHADER.Value);
            _pixelShader = device.CreatePixelShader(PIXEL_SHADER.Value);
            _constants = device.CreateBuffer(new BufferDescription(Marshal.SizeOf<Constants>(), BindFlags.ConstantBuffer));
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    #endregion

    #region Methods

    /// <summary>
    /// Converts an SDR white level to the factor the frame is multiplied with, so SDR white becomes 1.0.
    /// </summary>
    /// <param name="sdrWhiteLevel">The luminance of SDR white on the display, in nits. Invalid values (not positive or not finite) use 80 nits (no scaling).</param>
    public static float GetWhiteScale(float sdrWhiteLevel)
        => (float.IsFinite(sdrWhiteLevel) && (sdrWhiteLevel > 0)) ? SCRGB_WHITE_NITS / sdrWhiteLevel : 1;

    /// <summary>
    /// Queues the tone mapping of <paramref name="frame"/> into <paramref name="target"/> (both the same size) on the GPU.
    /// </summary>
    /// <param name="context">The immediate context of the device this was created with.</param>
    /// <param name="frame">A view of the HDR frame.</param>
    /// <param name="target">A view of the 8-bit target texture.</param>
    /// <param name="width">The width of both textures.</param>
    /// <param name="height">The height of both textures.</param>
    /// <param name="whiteScale">The factor the frame is multiplied with (see <see cref="GetWhiteScale"/>).</param>
    public void Draw(ID3D11DeviceContext context, ID3D11ShaderResourceView frame, ID3D11RenderTargetView target, int width, int height, float whiteScale)
    {
        // The value is the same for most frames: the buffer is only updated if the user changed the SDR brightness.
        if (!whiteScale.Equals(_whiteScale))
        {
            Constants constants = new() { WhiteScale = whiteScale };
            context.UpdateSubresource(in constants, _constants);
            _whiteScale = whiteScale;
        }

        // The device only draws this: no other state (input layout, blend, rasterizer) is ever changed from its default.
        context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        context.VSSetShader(_vertexShader);
        context.PSSetShader(_pixelShader);
        context.PSSetShaderResource(0, frame);
        context.PSSetConstantBuffer(0, _constants);
        context.RSSetViewport(0, 0, width, height);
        context.OMSetRenderTargets(target);

        context.Draw(3, 0);

        // Unbound, so the textures can be written (frame) and read (target) by copies without a binding conflict.
        context.PSSetShaderResource(0, null!);
        context.OMSetRenderTargets((ID3D11RenderTargetView)null!);
    }

    private static byte[] ReadShader(string name)
    {
        using Stream stream = typeof(HdrToneMapper).Assembly.GetManifestResourceStream(name)
                              ?? throw new InvalidOperationException($"The embedded shader '{name}' is missing.");

        byte[] bytecode = new byte[stream.Length];
        stream.ReadExactly(bytecode);

        return bytecode;
    }

    public void Dispose()
    {
        _constants?.Dispose();
        _pixelShader?.Dispose();
        _vertexShader?.Dispose();
    }

    #endregion

    // A constant buffer is a multiple of 16 bytes.
    [StructLayout(LayoutKind.Sequential, Size = 16)]
    private struct Constants
    {
        public float WhiteScale;
    }
}
