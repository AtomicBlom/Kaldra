// Tonemap / grade (Planning.md §12 M5). The consumer of the auto-exposure compute: it samples the bloomed
// scene (binding 0) and the 1×1 exposure the compute wrote (binding 1), scales by exposure, and applies a
// gentle Reinhard shoulder so the now-exposed HDR rolls off instead of hard-clipping on the LDR backbuffer.
// Runs in the world domain before the nearest upscale, so it stays pixel-consistent.

#include "kaldra_platform.module.slang"

Sampler2D sceneTex;    // bloomed scene (HDR)
Sampler2D exposureTex; // 1×1 auto-exposure multiplier

// hdr != 0 selects the HDR-display output path (M7-B6): the engine sets it from the negotiated swapchain
// (ctx.HdrOutput). On an scRGB (linear) swapchain the terminal pass writes these values straight through, so
// the only difference HDR makes is here — preserve the extended range instead of compressing it to SDR.
struct PushConstants { float hdr; };
KALDRA_PUSH_CONSTANT PushConstants pc;

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
	output.position = KALDRA_FULLSCREEN_POS(uv);
	return output;
}

[shader("fragment")]
float4 fragmentMain(VsOut input) : SV_Target
{
	float exposure = exposureTex.Sample(float2(0.5, 0.5)).r;
	float3 c = sceneTex.Sample(input.uv).rgb * exposure;

	if (pc.hdr > 0.5)
	{
		// HDR output (scRGB linear, 1.0 == 80 nits): don't compress — scale SDR-white to a comfortable paper
		// white so mid-tones sit at ~200 nits and the bright cube + glow push above paper white as real HDR
		// highlights (clamped only by the display's peak). No transfer-function encode: scRGB is linear.
		const float paperWhite = 2.5; // ~200-nit paper white relative to scRGB's 80-nit unit
		return float4(c * paperWhite, 1.0);
	}

	c = c / (1.0 + c * 0.5); // SDR: gentle Reinhard shoulder — keeps the bright cube + glow from clipping flat
	return float4(c, 1.0);
}
