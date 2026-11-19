// Tone maps an HDR desktop frame (scRGB: linear, BT.709 primaries, 1.0 = 80 nits, R16G16B16A16_FLOAT) to 8-bit sRGB.
// Drawn as one triangle covering the target, which has the size of the frame: every target pixel reads the frame pixel at the same position.
//
// Compiled with fxc to the embedded *.cso files (no shader compiler at runtime); run Compile.cmd after changing this file.
// HdrToneMappingTests checks that the embedded bytecode matches this source.

Texture2D<float4> Frame : register(t0);

cbuffer ToneMapping : register(b0)
{
    // 80 nits / SDR white level of the display: SDR white (e.g. 200 nits = 2.5 in scRGB) becomes 1.0.
    float WhiteScale;
};

float4 VSMain(uint id : SV_VertexID) : SV_Position
{
    // (-1, 1), (3, 1), (-1, -3): a triangle covering the whole viewport.
    float2 position = float2((id << 1) & 2, id & 2);
    return float4(position * float2(2, -2) + float2(-1, 1), 0, 1);
}

float4 PSMain(float4 position : SV_Position) : SV_Target
{
    float3 color = Frame.Load(int3(position.xy, 0)).rgb * WhiteScale;

    // Brighter than SDR white: scaled down as a whole, so the hue stays (instead of clipping each channel, which shifts it towards white).
    // Negative values are colors outside of the sRGB gamut (wide gamut content): they don't count for the peak and are clamped to 0 (the edge of the gamut).
    float peak = max(color.r, max(color.g, color.b));
    color = saturate(color / max(peak, 1));

    // sRGB encoding (IEC 61966-2-1); the 8-bit target rounds to the nearest value.
    float3 srgb = (color <= 0.0031308) ? (color * 12.92) : ((1.055 * pow(color, 1.0 / 2.4)) - 0.055);
    return float4(srgb, 1);
}
