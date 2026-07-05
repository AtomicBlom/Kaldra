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
