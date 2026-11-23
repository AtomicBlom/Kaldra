// Dear ImGui render shader (the imgui_impl_vulkan equivalent). The ImGuiRenderer feeds it ImDrawVert
// vertices (pixel-space position, uv, packed RGBA8 colour) and a scale+translate vertex push that maps
// ImGui's pixel coordinates to clip space. The fragment multiplies the vertex colour by the font-atlas
// texture. Alpha-blended over the final image as the screen-space HUD (Planning.md §6/§9: the HUD is native
// resolution, on top, untouched by bloom/pixelation).

#include "kaldra_platform.module.slang"

struct PushConstants
{
	float2 scale;
	float2 translate;
};

KALDRA_PUSH_CONSTANT PushConstants pc;

Sampler2D fontTex;

struct VsIn
{
	// HLSL semantics alongside the vk::location qualifiers (see lit.slang): one cross-target source — Slang pins
	// the SPIR-V locations (Vulkan) AND emits the semantics the HLSL/DXIL target (DX12) needs.
	[[vk::location(0)]] float2 position : POSITION; // ImGui pixel coordinates
	[[vk::location(1)]] float2 uv       : TEXCOORD0;
	[[vk::location(2)]] float4 colour   : COLOR0;   // R8G8B8A8_UNORM → [0,1]
};

struct VsOut
{
	float4 position : SV_Position;
	float2 uv : TEXCOORD0;
	float4 colour : COLOR0;
};

[shader("vertex")]
VsOut vertexMain(VsIn input)
{
	VsOut output;
	output.position = float4(input.position * pc.scale + pc.translate, 0.0, 1.0);
	output.uv = input.uv;
	output.colour = input.colour;
	return output;
}

[shader("fragment")]
float4 fragmentMain(VsOut input) : SV_Target
{
	return input.colour * fontTex.Sample(input.uv);
}
