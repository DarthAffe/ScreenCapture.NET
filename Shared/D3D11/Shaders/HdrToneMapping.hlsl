// Tone maps an HDR frame (scRGB: linear, BT.709, 1.0 = 80 nits) to 8-bit sRGB. Frame and target have the same size.
//
// Run Compile.cmd after changing this file. HdrToneMappingTests checks the embedded bytecode against it.

Texture2D<float4> Frame : register(t0);

cbuffer ToneMapping : register(b0)
{
    // 80 nits / SDR white level, so SDR white becomes 1.0.
    float WhiteScale;
};

float4 VSMain(uint id : SV_VertexID) : SV_Position
{
    // One triangle covering the viewport: (-1, 1), (3, 1), (-1, -3).
    float2 position = float2((id << 1) & 2, id & 2);
    return float4(position * float2(2, -2) + float2(-1, 1), 0, 1);
}

float4 PSMain(float4 position : SV_Position) : SV_Target
{
    float3 color = Frame.Load(int3(position.xy, 0)).rgb * WhiteScale;

    // Brighter than SDR white is scaled down as a whole to keep the hue. Negative values are outside the sRGB gamut and clamped.
    float peak = max(color.r, max(color.g, color.b));
    color = saturate(color / max(peak, 1));

    // sRGB encoding (IEC 61966-2-1).
    float3 srgb = (color <= 0.0031308) ? (color * 12.92) : ((1.055 * pow(color, 1.0 / 2.4)) - 0.055);
    return float4(srgb, 1);
}
