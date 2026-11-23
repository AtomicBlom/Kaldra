// Per-target macros. The same Slang source compiles to SPIR-V, DXIL, and WGSL.
//
// Resources name no binding, slot or register. Slang gives each target its own numbers in declaration order, written to
// a reflection sidecar per target (<name>.json, .dxil.json, .wgsl.json), and every backend binds by what that sidecar
// says: Vulkan by SPIR-V binding, DirectX 12 by register (its root signature built at them), WebGPU by WGSL binding.
// What a pass supplies fills a shader's slots in declaration order, kind by kind (sampled textures, storage buffers,
// uniform buffers), so declaration order is the one thing a shader states.
//
// The push-constant block is the exception: WGSL has none, so KALDRA_WGSL lowers it to an ordinary uniform parameter.
#ifdef KALDRA_WGSL
#define KALDRA_PUSH_CONSTANT uniform
#else
#define KALDRA_PUSH_CONSTANT [[vk::push_constant]]
#endif

// Clip-space position for a fullscreen-triangle pass, from its 0..2 uv. A fullscreen technique fabricates its
// triangle from SV_VertexID and never goes through the camera's clip-space correction, so the NDC-Y convention
// has to be baked in here: Vulkan/DX12 map uv 0..1 straight to NDC -1..1 (their Y-down NDC, plus DirectX12's
// negative-height viewport, put uv-row 0 at the top), but WebGPU has a Y-up NDC and no negative viewport, so its
// fullscreen triangle must flip Y — otherwise every sampled post pass comes out upside down. Sampled uv is
// unchanged; only the emitted position differs.
#ifdef KALDRA_WGSL
#define KALDRA_FULLSCREEN_POS(uv) float4((uv).x * 2.0 - 1.0, 1.0 - (uv).y * 2.0, 0.0, 1.0)
#else
#define KALDRA_FULLSCREEN_POS(uv) float4((uv) * 2.0 - 1.0, 0.0, 1.0)
#endif

// Clip-space position for a vertex already authored in clip space — a shader that fabricates its geometry from
// SV_VertexID with no camera to carry the backend's clip-space correction. Same NDC-Y split as
// KALDRA_FULLSCREEN_POS, and for the same reason; this one just takes NDC xy directly (-1..1) instead of a 0..1 uv.
#ifdef KALDRA_WGSL
#define KALDRA_CLIP_POS(xy) float4((xy).x, -(xy).y, 0.0, 1.0)
#else
#define KALDRA_CLIP_POS(xy) float4((xy), 0.0, 1.0)
#endif

// Screen uv (0..1, top-left origin) of a clip-space position, for a shader that reprojects a world point back to
// screen to sample a G-buffer there (SSAO, screen-space reflections, …). Same NDC-Y convention split as
// KALDRA_FULLSCREEN_POS: Vulkan/DX12 carry the clip-space correction in their view-projection so uv.y = ndc.y·0.5+0.5,
// but WebGPU renders with an uncorrected (Y-up NDC) view-projection, so the same screen row is at the opposite ndc.y
// and uv.y must flip. uv.x is unaffected (the correction only flips Y). Takes the raw clip vector (before the
// perspective divide) and divides internally.
#ifdef KALDRA_WGSL
#define KALDRA_CLIP_TO_UV(clip) float2((clip).x / (clip).w * 0.5 + 0.5, 0.5 - (clip).y / (clip).w * 0.5)
#else
#define KALDRA_CLIP_TO_UV(clip) (((clip).xy / (clip).w) * 0.5 + 0.5)
#endif

// Clip-space position for a 2D overlay/UI vertex, from its surface-pixel position and the pixel->clip scale +
// translate push (2/w, 2/h, -1, -1). Like a fullscreen pass, an overlay maps to clip space directly rather than
// through the camera's clip-space correction, and the same NDC-Y split applies: Vulkan's Y-down NDC (and DX12's
// negative-height viewport) put pixel-row 0 at the top with the plain mapping, but WebGPU's Y-up NDC + no negative
// viewport must flip the resulting clip Y — otherwise the whole overlay renders upside down. Only clip Y differs.
#ifdef KALDRA_WGSL
#define KALDRA_OVERLAY_POS(px, scale, translate) float4((px).x * (scale).x + (translate).x, -((px).y * (scale).y + (translate).y), 0.0, 1.0)
#else
#define KALDRA_OVERLAY_POS(px, scale, translate) float4((px) * (scale) + (translate), 0.0, 1.0)
#endif
