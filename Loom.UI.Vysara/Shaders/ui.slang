// Loom game-UI render shader — the twin of imgui.slang for the XAML UI engine. The backend feeds it
// OverlayVertex vertices (surface-pixel position, uv, premultiplied RGBA16F colour, per-prim params) and a
// scale+translate vertex push that maps surface pixels to clip space (the ImGui push).
// One module, one fragment that switches on a flat `uint kind` (the UIPrimKind the DrawCommand carried):
//   Fill        — solid quad (no SDF, no texture).
//   RoundedRect — analytic rounded-box SDF in local rect space, one-pixel edge coverage.
//   Border      — analytic rounded-box ring (outer minus inner), one-pixel edge coverage.
//   Glyph       — MTSDF text: median(rgb) distance, screenPxRange-scaled coverage. An outline or shadow quad reads
//                 the true distance in .a instead, its edge moved out and softened by the quad's params.
//   Image       — sample the atlas/source texture, tint by the vertex colour.
//   Vector      — one triangle of a tessellated stroke or fill: the product of up to four half-plane edge
//                 coverages, times the vertex colour and the bound texture (the white texel for a solid paint).
// Colours are PREMULTIPLIED (the compositor folds opacity in), so the renderer blends premultiplied alpha-over
// (src=ONE, dst=ONE_MINUS_SRC_ALPHA) — not ImGui's straight-alpha.
// A texture reads as its source declares. The colour space is the bound format's: an sRGB image binds an sRGB format,
// so the sampler decodes each texel to linear light before filtering, while the atlas and the white texel bind
// linear/UNORM (SDF distances are never decoded). The alpha mode rides the command: STRAIGHT_ALPHA in its tag means the
// slot's texels are straight, so the arms that paint with a slot (image, vector) premultiply the filtered sample.
// Vysara's software rasterizer is this fragment's CPU oracle: each arm has a twin there computing the same thing.

// UIPrimKind (Vysara) — keep in sync. Values 0..4 and 7 are the shaded primitive families this fragment switches
// on. Values 5/6 (BeginLayer/EndLayer) are zero-geometry group-opacity markers the backend turns into an offscreen
// render-to-texture composite below the graph seam; they never reach this shader, and the composite blit reuses
// KIND_IMAGE (sample the resolved layer, tint by the group alpha). A layer effect id rides the BeginLayer marker
// for a blur branch; only effect id 0 (opacity only) exists, so no KIND_LAYER_* branch does.
#include "loom_platform.module.slang"

static const uint KIND_FILL = 0;
static const uint KIND_ROUNDED_RECT = 1;
static const uint KIND_BORDER = 2;
static const uint KIND_GLYPH = 3;
static const uint KIND_IMAGE = 4;
static const uint KIND_VECTOR = 7;

// A command's tag is its kind in the low byte plus flags above it. STRAIGHT_ALPHA marks an image or vector command whose
// slot holds straight-alpha texels; UIPainter sets it from the slot's source, and Vysara's kinds never reach it.
static const uint KIND_MASK = 0xFF;
static const uint STRAIGHT_ALPHA = 0x100;

// The smallest gradient length edgeCoverage divides by. A distance that does not vary across its triangle — an
// unused Vector slot, which holds a large positive "inside" value — would otherwise divide by zero.
static const float MIN_GRADIENT = 1e-6;

struct PushConstants
{
	float2 scale;          // surface-pixel -> clip scale (2/w, -2/h style; the backend supplies the sign)
	float2 translate;      // surface-pixel -> clip translate
	float  paperWhite;     // HDR: linear scale = UIPaperWhiteNits / 80; 1.0 on SDR
	uint   isHdr;          // 1 when the target is scRGB-HDR (apply paperWhite), else 0
	uint   kind;           // The command's tag: its UIPrimKind in the low byte, STRAIGHT_ALPHA above. Last in the block:
	                       // the draw-list verb rewrites the final push slot per command with the tag, so it sits here.
};

LOOM_PUSH_CONSTANT PushConstants pc;

// One combined sampler, bound per command to the white texel (fills) or an atlas page (glyph/image) — mirrors
// ImGui's per-command texture bind. Slang lowers Sampler2D to a Texture2D+SamplerState pair for the DXIL target.
Sampler2D tex;

struct VsIn
{
	// HLSL semantics alongside vk::location (see lit.slang): one cross-target source — Slang pins the SPIR-V
	// locations (Vulkan) and emits the semantics the HLSL/DXIL target (DX12) needs.
	[[vk::location(0)]] float2 position : POSITION;  // surface pixels (post-transform, post-DPI)
	[[vk::location(1)]] float2 uv       : TEXCOORD0; // glyph/image atlas uv, OR 0..1 across the rect (SDF)
	[[vk::location(2)]] float4 colour   : COLOR0;    // premultiplied RGBA16F
	[[vk::location(3)]] float4 params   : TEXCOORD1; // rect: (cornerRadius, borderThickness, sizeX, sizeY); glyph: (screenPxRange, bias, softness, 0)
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

// Coverage of an edge at a signed distance d, positive inside, ramped linearly across exactly one screen pixel.
// Dividing d by the length of its own screen-space gradient puts it in pixels whatever its units and whatever the
// edge's angle, so the ramp is as wide at 45° as at 0°. fwidth would not do: |ddx| + |ddy| is the L1 norm, which
// over-states the gradient by up to √2 on a diagonal and so blurs it.
// Derivatives are legal only in uniform control flow (WGSL rejects them anywhere else), so call this only straight
// from an arm of fragmentMain's switch on pc.kind, which is a push constant and so uniform.
float edgeCoverage(float d)
{
	return saturate(0.5 + d / max(length(float2(ddx(d), ddy(d))), MIN_GRADIENT));
}

float median3(float3 v) { return max(min(v.r, v.g), min(max(v.r, v.g), v.b)); }

// The premultiplied colour the bound slot paints with: the decoded, filtered sample, multiplied by its own alpha when the
// slot is straight. The premultiply comes after the filter, as the rasterizer's does, so a straight image's transparent
// texels (whose colour is arbitrary) never bleed into its edges. Call only from a switch arm, for the same uniformity
// reason as edgeCoverage: Sample takes implicit derivatives.
float4 slotColour(float2 uv)
{
	float4 s = tex.Sample(uv);
	return (pc.kind & STRAIGHT_ALPHA) != 0 ? float4(s.rgb * s.a, s.a) : s;
}

[shader("fragment")]
float4 fragmentMain(VsOut input) : SV_Target
{
	float4 colour = input.colour;

	switch (pc.kind & KIND_MASK)
	{
	case KIND_ROUNDED_RECT:
	{
		float2 size = input.params.zw;
		float2 p = input.uv * size - size * 0.5;
		colour *= edgeCoverage(-sdRoundedBox(p, size * 0.5, input.params.x));
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
		colour *= saturate(edgeCoverage(-dOuter) - edgeCoverage(-dInner));
		break;
	}
	case KIND_GLYPH:
	{
		// params = (screenPxRange, bias, softness, 0), bias and softness in units of the atlas's distance range. Both 0
		// draws the glyph itself, from the median of the RGB distances. Otherwise the quad is an outline or a shadow, read
		// from the true distance in A: its edge moved out by the bias and ramped over the softness, never under one pixel.
		// The quad's params pick the arm by select, not by branch, so the sample stays in uniform control flow.
		float4 s = tex.Sample(input.uv);
		float screenPxRange = input.params.x;
		float bias = input.params.y;
		float softness = input.params.z;
		bool effect = any(input.params.yz > 0.0);
		float sd = select(effect, s.a - 0.5 + bias, median3(s.rgb) - 0.5);
		float k = select(effect, min(screenPxRange, 1.0 / max(softness, 1e-6)), screenPxRange);
		colour *= saturate(sd * k + 0.5);
		break;
	}
	case KIND_IMAGE:
		colour *= slotColour(input.uv);
		break;
	case KIND_VECTOR:
	{
		// params = four signed distances to the half-planes bounding this triangle's shape, positive inside and each
		// linear across the triangle: a stroke quad's two sides and two cap or miter clips, or a fill fringe's one
		// edge. An unused slot holds a large positive value, so its coverage is 1.
		float4 d = input.params;
		float coverage = edgeCoverage(d.x) * edgeCoverage(d.y) * edgeCoverage(d.z) * edgeCoverage(d.w);
		colour *= slotColour(input.uv) * coverage;
		break;
	}

	default: // KIND_FILL — solid quad.
		break;
	}

	// HDR: lift premultiplied UI colour from SDR reference white to the configured paper-white luminance.
	if (pc.isHdr != 0)
		colour.rgb *= pc.paperWhite;

	return colour;
}
