// Runtime debug visualization / terminal upscale (Planning.md §6 "upscale" + §7.6 debug viz). This is the
// terminal blit, but instead of sampling one input it samples EVERY inspectable buffer and a push-constant
// selects which to present. That keeps the graph immutable (Planning.md §8 — only a uniform varies per frame,
// the read edges are fixed) while letting the HUD's View menu switch the visualized buffer at runtime. Mode 0
// is the normal path: a nearest sample of the final graded image (the §9 pixelation upscale). The samplers are
// bound in the order the pass declares .Samples(...), which matches the View-menu index order.

#include "loom_platform.module.slang"

struct PushConstants
{
	float mode; // which buffer to present; index matches the host's view list
};

LOOM_PUSH_CONSTANT PushConstants pc;

LOOM_BINDING(0) Sampler2D graded;    // 0 Final (the tonemapped, exposure-graded image)
LOOM_BINDING(1) Sampler2D albedo;    // 1
LOOM_BINDING(2) Sampler2D normalTex; // 2 (stored *0.5+0.5, so the raw sample IS the classic normal view)
LOOM_BINDING(3) Sampler2D position; // 3 world position
LOOM_BINDING(4) Sampler2D depthTex;  // 4 perspective depth
LOOM_BINDING(5) Sampler2D ao;        // 5 SSAO (single channel)
LOOM_BINDING(6) Sampler2D lit;       // 6 deferred lit (HDR)
LOOM_BINDING(7) Sampler2D outlined;  // 7 lit + JFA hover ring (HDR)
LOOM_BINDING(8) Sampler2D glow;      // 8 bloom result (HDR)
LOOM_BINDING(9) Sampler2D shadowTex; // 9 sun shadow map (ortho depth)

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

// Linearize a perspective depth sample to a [0,1] grey ramp (the sandbox camera is near=0.1, far=100).
// Clustered raw depth reads as flat white; this makes the gradient legible. Coupling to the camera's near/far
// is fine here — this is the sandbox's debug view, not engine code.
float linearizeDepth(float d)
{
	const float near = 0.1;
	const float far = 100.0;
	float linear = (near * far) / (far - d * (far - near));
	return saturate(linear / 8.0); // objects within ~8 units span the ramp
}

[shader("fragment")]
float4 fragmentMain(VsOut input) : SV_Target
{
	float2 uv = input.uv;
	int mode = (int)(pc.mode + 0.5);
	switch (mode)
	{
	case 1: return float4(albedo.Sample(uv).rgb, 1.0);
	case 2: return float4(normalTex.Sample(uv).rgb, 1.0);
	case 3: return float4(frac(position.Sample(uv).xyz), 1.0);             // world position as a repeating pattern
	case 4: return float4(linearizeDepth(depthTex.Sample(uv).r).xxx, 1.0);
	case 5: return float4(ao.Sample(uv).rrr, 1.0);
	case 6: return float4(lit.Sample(uv).rgb, 1.0);
	case 7: return float4(outlined.Sample(uv).rgb, 1.0);
	case 8: return float4(glow.Sample(uv).rgb, 1.0);
	case 9: return float4(shadowTex.Sample(uv).rrr, 1.0);
	default: return float4(graded.Sample(uv).rgb, 1.0);                    // 0 = Final
	}
}
