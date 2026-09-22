cbuffer Pose : register(b0, space1) { row_major float4x4 viewProjection; row_major float4x4 bones[40]; };
cbuffer Outline : register(b1, space1) { float4 outlineParameters; };
struct Input { float3 position : TEXCOORD0; float3 normal : TEXCOORD1; float2 uv : TEXCOORD2; float4 color : TEXCOORD3; float4 weights : TEXCOORD4; float4 boneIndices : TEXCOORD5; };
struct Output { float4 position : SV_Position; float2 uv : TEXCOORD0; float4 color : TEXCOORD1; float facing : TEXCOORD2; };
Output main(Input input) {
    row_major float4x4 skin = bones[(int)input.boneIndices.x] * input.weights.x
        + bones[(int)input.boneIndices.y] * input.weights.y
        + bones[(int)input.boneIndices.z] * input.weights.z
        + bones[(int)input.boneIndices.w] * input.weights.w;
    float3 worldPosition = mul(float4(input.position, 1), skin).xyz;
    float3 worldNormal = mul(float4(input.normal, 0), skin).xyz;
    float4 clip = mul(float4(worldPosition, 1), viewProjection);
    float2 projectedNormal = mul(float4(worldNormal, 0), viewProjection).xy;
    if (outlineParameters.x > 0 && dot(projectedNormal, projectedNormal) > 1e-12)
        clip.xy += normalize(projectedNormal / outlineParameters.yz)
            * outlineParameters.x * outlineParameters.yz * input.color.g * clip.w;
    Output output;
    output.position = clip;
    output.uv = input.uv;
    output.color = input.color;
    output.facing = dot(normalize(worldNormal), -normalize(float3(viewProjection._14, viewProjection._24, viewProjection._34)));
    return output;
}
