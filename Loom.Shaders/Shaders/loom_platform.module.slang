// Per-target resource-binding macros. The same Slang source compiles to SPIR-V, DXIL, and WGSL,
// but the targets disagree about explicit bindings:
//
//  - SPIR-V/DXIL: each resource carries an explicit [[vk::binding]] slot, and push constants use
//    the dedicated push-constant block.
//  - WGSL: there are no push constants, and a combined Sampler2D occupies TWO binding slots
//    (texture + sampler), so the explicit slot numbers from the Vulkan scheme would collide.
//
// Compiling with LOOM_WGSL defined therefore drops the explicit numbers (Slang assigns unique
// slots in declaration order) and lowers the push-constant block to an ordinary uniform
// parameter. The WebGPU backend recovers the actual assignments from the emitted WGSL text, so
// no code shares slot numbers with this scheme.
#ifdef LOOM_WGSL
#define LOOM_BINDING(slot)
#define LOOM_PUSH_CONSTANT uniform
#else
#define LOOM_BINDING(slot) [[vk::binding(slot, 0)]]
#define LOOM_PUSH_CONSTANT [[vk::push_constant]]
#endif

// Clip-space position for a fullscreen-triangle pass, from its 0..2 uv. A fullscreen technique fabricates its
// triangle from SV_VertexID and never goes through the camera's clip-space correction, so the NDC-Y convention
// has to be baked in here: Vulkan/DX12 map uv 0..1 straight to NDC -1..1 (their Y-down NDC, plus DirectX12's
// negative-height viewport, put uv-row 0 at the top), but WebGPU has a Y-up NDC and no negative viewport, so its
// fullscreen triangle must flip Y — otherwise every sampled post pass comes out upside down. Sampled uv is
// unchanged; only the emitted position differs.
#ifdef LOOM_WGSL
#define LOOM_FULLSCREEN_POS(uv) float4((uv).x * 2.0 - 1.0, 1.0 - (uv).y * 2.0, 0.0, 1.0)
#else
#define LOOM_FULLSCREEN_POS(uv) float4((uv) * 2.0 - 1.0, 0.0, 1.0)
#endif
