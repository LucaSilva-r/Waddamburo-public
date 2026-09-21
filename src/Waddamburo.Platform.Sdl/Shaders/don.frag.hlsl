Texture2D<float4> materialTexture : register(t0, space2);
SamplerState materialSampler : register(s0, space2);
cbuffer Material : register(b0, space3) { float4 parameters; float4 replaceRed; float4 replaceGreen; float4 replaceBlue; };
struct Input { float4 position : SV_Position; float2 uv : TEXCOORD0; float4 color : TEXCOORD1; float facing : TEXCOORD2; };
float4 main(Input input) : SV_Target0 {
    float4 sampled = materialTexture.Sample(materialSampler, input.uv);
    int kind = (int)parameters.y;
    float4 result = sampled;
    if (kind == 1)
        result = float4(sampled.r * replaceRed.rgb + sampled.g * replaceGreen.rgb + sampled.b * replaceBlue.rgb, sampled.a);
    else if (kind == 3 || kind == 8) {
        if (input.facing > 0.3) discard;
        result = float4(0, 0, 0, sampled.a);
    }
    if (result.a <= parameters.x) discard;
    return result;
}
