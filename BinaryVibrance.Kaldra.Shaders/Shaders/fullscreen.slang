// Fullscreen-triangle technique (Planning.md §5 `.FullScreenTriangle()`).
//
// No vertex buffer: the vertex shader fabricates a single oversized triangle from
// SV_VertexID (the gl_VertexIndex trick), so a 3-vertex non-indexed draw covers the
// whole render target. Slang compiles both entry points below into one SPIR-V module;
// the bake step (M1) selects them by name (`vertexMain` / `fragmentMain`).

#include "kaldra_platform.module.slang"

struct VsOut
{
	float4 position : SV_Position;
	float2 uv : TEXCOORD0;
};

[shader("vertex")]
VsOut vertexMain(uint vertexId : SV_VertexID)
{
	// vertexId 0,1,2 -> uv (0,0),(2,0),(0,2) -> clip (-1,-1),(3,-1),(-1,3).
	float2 uv = float2((vertexId << 1) & 2, vertexId & 2);

	VsOut output;
	output.uv = uv;
	output.position = KALDRA_FULLSCREEN_POS(uv);
	return output;
}

[shader("fragment")]
float4 fragmentMain(VsOut input) : SV_Target
{
	// A simple gradient, so a successful draw is unmistakable on screen.
	return float4(input.uv, 0.5, 1.0);
}
