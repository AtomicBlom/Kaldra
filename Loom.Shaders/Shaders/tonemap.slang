// Tonemap / grade (Planning.md §12 M5). The consumer of the auto-exposure compute: it samples the bloomed
// scene (binding 0) and the 1×1 exposure the compute wrote (binding 1), scales by exposure, and applies a
// gentle Reinhard shoulder so the now-exposed HDR rolls off instead of hard-clipping on the LDR backbuffer.
// Runs in the world domain before the nearest upscale, so it stays pixel-consistent.

[[vk::binding(0, 0)]] Sampler2D sceneTex;    // bloomed scene (HDR)
[[vk::binding(1, 0)]] Sampler2D exposureTex; // 1×1 auto-exposure multiplier

struct VsOut
{
	float4 position : SV_Position;
	float2 uv : TEXCOORD0;
};

[shader("vertex")]
VsOut vertexMain(uint vertexId : SV_VertexID)
{
	float2 uv = float2((vertexId << 1) & 2, vertexId & 2);

	VsOut output;
	output.uv = uv;
	output.position = float4(uv * 2.0 - 1.0, 0.0, 1.0);
	return output;
}

[shader("fragment")]
float4 fragmentMain(VsOut input) : SV_Target
{
	float exposure = exposureTex.Sample(float2(0.5, 0.5)).r;
	float3 c = sceneTex.Sample(input.uv).rgb * exposure;
	c = c / (1.0 + c * 0.5); // gentle Reinhard shoulder — keeps the bright cube + glow from clipping flat
	return float4(c, 1.0);
}
