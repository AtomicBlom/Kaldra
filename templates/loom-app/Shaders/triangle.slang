// HelloTriangle spike: a hardcoded, vertex-ID-fabricated triangle — no vertex buffer, no camera, no material.
// The smallest possible technique that still exercises the real render-graph + backend pipeline end to end.
// Modeled on Loom.Shaders/Shaders/fullscreen.slang's SV_VertexID fabrication trick.

#include "loom_platform.module.slang"

struct VsOut
{
	float4 position : SV_Position;
	float3 color : COLOR0;
};

[shader("vertex")]
VsOut vertexMain(uint vertexId : SV_VertexID)
{
	float2 positions[3] = { float2(0.0, 0.75), float2(-0.75, -0.75), float2(0.75, -0.75) };
	float3 colors[3] = { float3(1.0, 0.2, 0.2), float3(0.2, 1.0, 0.2), float3(0.2, 0.4, 1.0) };

	float2 pos = positions[vertexId];

	VsOut output;
	output.color = colors[vertexId];
	// Fabricated directly in clip space (no camera to carry the backend's clip-space correction), so the NDC-Y
	// split has to be applied here — same convention as LOOM_FULLSCREEN_POS: Vulkan/DX12 map straight through;
	// WebGPU's Y-up NDC needs the flip.
#ifdef LOOM_WGSL
	output.position = float4(pos.x, -pos.y, 0.0, 1.0);
#else
	output.position = float4(pos, 0.0, 1.0);
#endif
	return output;
}

[shader("fragment")]
float4 fragmentMain(VsOut input) : SV_Target
{
	return float4(input.color, 1.0);
}
