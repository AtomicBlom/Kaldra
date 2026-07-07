// Loom game-UI render shader (PlanningUI.md §D2.7) — the twin of imgui.slang for the XAML UI engine. The
// backend UIRenderer feeds it UIVertex vertices (surface-pixel position, uv, premultiplied RGBA16F colour,
// per-prim params) and a scale+translate vertex push that maps surface pixels to clip space (the ImGui push).
// One module, one fragment that switches on a flat `uint kind` (the UIPrimKind the UIDrawCommand carried):
//   Fill        — solid quad (no SDF, no texture).
//   RoundedRect — analytic rounded-box SDF in local rect space, fwidth-AA coverage.
//   Border      — analytic rounded-box ring (outer minus inner), fwidth-AA coverage.
//   Glyph       — MTSDF text: median(rgb) distance, screenPxRange-scaled smoothstep coverage (§D3.4). The .a
//                 channel is a true SDF reserved for later outline/shadow/glow — unused here.
//   Image       — sample the atlas/source texture, tint by the vertex colour.
// Colours are PREMULTIPLIED (the compositor folds opacity in), so the renderer blends premultiplied alpha-over
// (src=ONE, dst=ONE_MINUS_SRC_ALPHA) — not ImGui's straight-alpha. The atlas binds linear/UNORM (SDF distances
// are linear, never sRGB-decoded).

// UIPrimKind (SUIsei) — keep in sync. Values 0..4 are the shaded primitive families this fragment switches on.
// Values 5/6 (BeginLayer/EndLayer) are zero-geometry group-opacity markers the backend turns into an offscreen
// render-to-texture composite below the graph seam; they never reach this shader, and the composite blit reuses
// KIND_IMAGE (sample the resolved layer, tint by the group alpha). A layer effect id rides the BeginLayer marker
// for a future blur branch — MU4 ships effect id 0 (opacity only), so no KIND_LAYER_* branch exists yet.
#include "loom_platform.module.slang"

static const uint KIND_FILL = 0;
static const uint KIND_ROUNDED_RECT = 1;
static const uint KIND_BORDER = 2;
static const uint KIND_GLYPH = 3;
static const uint KIND_IMAGE = 4;

struct PushConstants
{
	float2 scale;          // surface-pixel -> clip scale (2/w, -2/h style; the backend supplies the sign)
	float2 translate;      // surface-pixel -> clip translate
	float  paperWhite;     // HDR: linear scale = UIPaperWhiteNits / 80 (§D4.4); 1.0 on SDR
	uint   isHdr;          // 1 when the target is scRGB-HDR (apply paperWhite), else 0
	uint   kind;           // UIPrimKind for this draw command. Last in the block: the draw-list verb rewrites the
	                       // final push slot per command with the command's tag, so the per-command field sits here.
};

LOOM_PUSH_CONSTANT PushConstants pc;

// One combined sampler, bound per command to the white texel (fills) or an atlas page (glyph/image) — mirrors
// ImGui's per-command texture bind. Slang lowers Sampler2D to a Texture2D+SamplerState pair for the DXIL target.
LOOM_BINDING(0) Sampler2D tex;

struct VsIn
{
	// HLSL semantics alongside vk::location (see lit.slang): one cross-target source — Slang pins the SPIR-V
	// locations (Vulkan) and emits the semantics the HLSL/DXIL target (DX12) needs.
	[[vk::location(0)]] float2 position : POSITION;  // surface pixels (post-transform, post-DPI)
	[[vk::location(1)]] float2 uv       : TEXCOORD0; // glyph/image atlas uv, OR 0..1 across the rect (SDF)
	[[vk::location(2)]] float4 colour   : COLOR0;    // premultiplied RGBA16F
	[[vk::location(3)]] float4 params   : TEXCOORD1; // rect: (cornerRadius, borderThickness, sizeX, sizeY); glyph: (screenPxRange, …)
};

struct VsOut
{
	float4 position : SV_Position;
	float2 uv       : TEXCOORD0;
	float4 colour   : COLOR0;
	float4 params   : TEXCOORD1;
};

[shader("vertex")]
VsOut vertexMain(VsIn input)
{
	VsOut output;
	// Map surface pixels to clip space; WGSL flips clip Y (WebGPU has Y-up NDC + no negative viewport), desktop maps
	// straight (Vulkan Y-down NDC / DX12 negative viewport). The desktop expansion is byte-identical to the old form.
	output.position = LOOM_OVERLAY_POS(input.position, pc.scale, pc.translate);
	output.uv = input.uv;
	output.colour = input.colour;
	output.params = input.params;
	return output;
}

// Signed distance to a rounded box: p is the point relative to the box centre, b the half-extents, r the corner
// radius (iquilezles.org/articles/distfunctions2d). Negative inside.
float sdRoundedBox(float2 p, float2 b, float r)
{
	float2 q = abs(p) - b + r;
	return min(max(q.x, q.y), 0.0) + length(max(q, 0.0)) - r;
}

// Coverage from a signed distance with ~1px analytic antialiasing (fwidth screen-space derivative). Inside (d<0) → 1.
float aaCoverage(float d)
{
	float w = max(fwidth(d), 1e-4);
	return 1.0 - smoothstep(-w, w, d);
}

float median3(float3 v) { return max(min(v.r, v.g), min(max(v.r, v.g), v.b)); }

[shader("fragment")]
float4 fragmentMain(VsOut input) : SV_Target
{
	float4 colour = input.colour;

	switch (pc.kind)
	{
	case KIND_ROUNDED_RECT:
	{
		float2 size = input.params.zw;
		float2 p = input.uv * size - size * 0.5;
		colour *= aaCoverage(sdRoundedBox(p, size * 0.5, input.params.x));
		break;
	}
	case KIND_BORDER:
	{
		float2 size = input.params.zw;
		float2 p = input.uv * size - size * 0.5;
		float radius = input.params.x;
		float thick = input.params.y;
		float dOuter = sdRoundedBox(p, size * 0.5, radius);
		float dInner = sdRoundedBox(p, size * 0.5 - thick, max(radius - thick, 0.0));
		colour *= saturate(aaCoverage(dOuter) - aaCoverage(dInner));
		break;
	}
	case KIND_GLYPH:
	{
		float3 msdf = tex.Sample(input.uv).rgb;
		float sd = median3(msdf) - 0.5;
		colour *= saturate(sd * input.params.x + 0.5); // params.x = screenPxRange (per glyph run)
		break;
	}
	case KIND_IMAGE:
		colour *= tex.Sample(input.uv);
		break;

	default: // KIND_FILL — solid quad.
		break;
	}

	// HDR: lift premultiplied UI colour from SDR reference white to the configured paper-white luminance (§D4.4).
	if (pc.isHdr != 0)
		colour.rgb *= pc.paperWhite;

	return colour;
}
