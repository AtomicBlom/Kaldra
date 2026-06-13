// Fullscreen blit/sample technique (Planning.md §6 "upscale"/blit, M3d).
//
// Like fullscreen.slang it fabricates a fullscreen triangle from SV_VertexID (no vertex
// buffer), but the fragment shader SAMPLES an input texture rather than computing a gradient.
// This is the engine's "blit" — a fullscreen-triangle sample with a nearest sampler (so it
// doubles as the §9 pixelation upscale), NOT vkCmdBlitImage. The sampled resource is bound as
// a single combined image sampler at set 0, binding 0.

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

// set 0, binding 0: a combined image sampler (the resource declared via .Samples(input)).
[[vk::binding(0, 0)]]
Sampler2D source;

[shader("fragment")]
float4 fragmentMain(VsOut input) : SV_Target
{
	return source.Sample(input.uv);
}
