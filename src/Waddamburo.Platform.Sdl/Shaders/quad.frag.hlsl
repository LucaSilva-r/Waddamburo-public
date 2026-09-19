Texture2D<float4> sourceTexture : register(t0, space2);
SamplerState sourceSampler : register(s0, space2);

struct FragmentInput
{
    float4 position : SV_Position;
    float2 textureCoordinate : TEXCOORD0;
    float4 colorMultiply : TEXCOORD1;
    float4 colorAdd : TEXCOORD2;
};

float4 main(FragmentInput input) : SV_Target0
{
    float4 straightColor = sourceTexture.Sample(sourceSampler, input.textureCoordinate)
        * input.colorMultiply + input.colorAdd;
    straightColor = saturate(straightColor);
    return float4(straightColor.rgb * straightColor.a, straightColor.a);
}
