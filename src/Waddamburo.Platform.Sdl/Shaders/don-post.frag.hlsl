Texture2D<float4> characterTexture : register(t0, space2);
SamplerState characterSampler : register(s0, space2);
cbuffer Post : register(b0, space3) { float4 parameters; };
struct Input { float4 position : SV_Position; float2 uv : TEXCOORD0; };
float4 main(Input input) : SV_Target0 {
    float4 center = characterTexture.Sample(characterSampler, input.uv);
    float coverage = 0;
    for (int index = 0; index < 16; index++) {
        float angle = 6.28318530718 * (index + 0.5) / 16.0;
        float2 delta = float2(cos(angle), sin(angle)) * parameters.yz * parameters.x;
        coverage = max(coverage, characterTexture.Sample(characterSampler, input.uv + delta).a);
    }
    return float4(center.a > 0 ? center.rgb : float3(0, 0, 0), max(coverage, center.a));
}
