# Shader authoring

The Slang-side contract: what a shader must declare so Loom's backends can bind it, and why the rules are what they are.

[`api-vocabulary.md`](api-vocabulary.md) documents the C# surface. This is the other half — the part written in `.slang` — and until now it lived only in backend source comments, which is a poor place for a contract app authors have to satisfy.

One source compiles to **SPIR-V** (Vulkan), **signed DXIL** (DirectX 12), and **WGSL** (WebGPU). The targets disagree about binding in ways no single numbering scheme papers over, so the disagreements are quarantined in one file — `Loom.Shaders/Shaders/loom_platform.module.slang` — behind macros.

---

## 1. The platform macros

`#include "loom_platform.module.slang"` first, then use these instead of writing a target conditional yourself.

| Macro | Purpose |
|---|---|
| `LOOM_BINDING(n)` | The explicit descriptor slot. Expands to `[[vk::binding(n, 0)]]` for SPIR-V/DXIL and to **nothing** for WGSL. |
| `LOOM_PUSH_CONSTANT` | Marks the small inline scalar block. `[[vk::push_constant]]` for SPIR-V/DXIL; lowers to an ordinary `uniform` for WGSL, which has no push constants. |
| `LOOM_FULLSCREEN_POS(uv)` | Clip position for a fullscreen triangle fabricated from `SV_VertexID`, given its 0..2 uv. |
| `LOOM_CLIP_POS(xy)` | Clip position for geometry already authored in NDC (−1..1) — the same job as above for a shader that isn't fullscreen-shaped. |
| `LOOM_OVERLAY_POS(px, scale, translate)` | Clip position for a 2D overlay/UI vertex in surface pixels. |
| `LOOM_CLIP_TO_UV(clip)` | Screen uv (0..1, top-left origin) of a clip-space position, for reprojecting a world point to sample a G-buffer there. |

The four position macros all exist for one reason: **NDC-Y**. Vulkan and DirectX 12 present a Y-down NDC (DX12 via a negative-height viewport), WebGPU a Y-up one with no negative viewport. A shader that goes through the camera never notices, because `Camera.ViewProjection` composes `IEngineClipSpace.ClipSpaceCorrection`. A shader that writes clip space *directly* has no camera to carry that correction, so the convention has to be applied at the point of emission — which is what these macros do.

> **Invariant:** `loom_platform.module.slang` is the only shader in the repository containing an `#ifdef LOOM_WGSL`. If you find yourself writing one, the macro you need is missing — add it there rather than branching in place. `grep -rl LOOM_WGSL --include=*.slang` should return exactly that one file.

---

## 2. Two independent numbering schemes

The single most confusing thing about a Loom shader is that a resource carries **two unrelated numbers**, and they do not agree:

```hlsl
LOOM_BINDING(0) Sampler2D albedoTex;
LOOM_BINDING(1) ConstantBuffer<ViewProj> view : register(b1);
LOOM_BINDING(2) ConstantBuffer<PerDraw>  draw : register(b2);
```

- **`LOOM_BINDING(n)` counts *every descriptor*** in one set, in category order (§3). The sampler is 0, so the first constant buffer is 1.
- **`register(bN)` counts *only constant buffers*, and starts at `b1`.** `b0` is reserved (§4). The first constant buffer is `b1` whether or not samplers precede it.

They coincide only in a shader with no samplers, which is why the mistake survives so long in a post-processing shader and then breaks the first time someone adds a texture. Slang honours `[[vk::binding]]` for SPIR-V and `register(bN)` for DXIL, so both must be right; each target reads the one addressed to it.

---

## 3. Vulkan: one descriptor set, in category order

Everything lands in **set 0**, and `VulkanTechnique.CreateSetLayout` builds the layout in this order:

1. sampled textures (combined image samplers)
2. storage images (`RWTexture2D`)
3. storage buffers (structured / byte-address)
4. uniform buffers (`ConstantBuffer<T>`)

`LOOM_BINDING(n)` numbers must follow the same category order, ascending, with no gaps. The counts themselves come from reflection — you never state them in C# — but the *order* is a convention the shader has to observe, because the backend builds the layout from counts and assumes the shader numbered them this way.

Practically: **declare samplers first and constant buffers last.**

---

## 4. DirectX 12: why `b0` is reserved

The root signature is, in order: an optional 32-bit root-constants block at **`b0`**, then one root CBV per reflected constant buffer at **`b1..bN`**, then an optional SRV descriptor table (`t0..`) with a static sampler (`s0`), then any storage-buffer root SRVs.

`b0` belongs to the root-constants block — the vertex MVP push for a mesh technique, or the 16-float scalar block for a post pass. It is never a `ConstantBuffer<T>` you declare. That is the whole reason ordinary constant buffers start at `b1`.

---

## 5. Uniform slots are filled in a defined order

A technique's uniform buffers are filled, slot 0 upward:

1. the slot reflection identifies as the **per-material parameter block**, if the shader declares one (§7)
2. then the pass's `.Reads(...)` constants, **in declaration order**
3. then the per-draw block (`DrawCall.Uniforms` / `SetUniforms`)

So a pass declaring `.Reads(viewProj)` and a draw supplying a per-draw block gives the shader two constant buffers, view-projection first. Adding a second `.Reads` inserts it before the per-draw block — the per-draw block is always last.

---

## 6. WebGPU derives its own assignments — never hand-number for it

`LOOM_BINDING(n)` expands to nothing under WGSL, deliberately. A combined `Sampler2D` occupies **two** WGSL binding slots (texture + sampler), so the Vulkan numbers would collide. Slang assigns slots in declaration order and the WebGPU backend recovers the real assignments by parsing the emitted WGSL.

Consequences worth knowing: WGSL has no push constants (`LOOM_PUSH_CONSTANT` becomes a uniform), and vertex-input locations may be permuted relative to stream order, which is why the backend parses them rather than assuming.

---

## 7. The per-material parameter block

A shader opts into per-material parameters by declaring a constant buffer under the reserved name **`loomMaterial`**:

```hlsl
struct LoomMaterial
{
    float4 baseColor;
    float4 emissive;
    float4 metallicRoughness;
};

LOOM_BINDING(3) ConstantBuffer<LoomMaterial> loomMaterial : register(b3);
```

Reflection reports which uniform slot it is and how many bytes it occupies; the engine stages a material's bytes into that slot on every draw using it. Convention is to declare it last, but nothing depends on that — it binds at whatever slot reflection reports.

**Lay the struct out in 16-byte-aligned rows.** HLSL constant-buffer packing and WGSL uniform layout agree on `float4` and disagree on tighter shapes, so a bare `float3` or a trailing `float` lands at different offsets on different backends. Pack loose scalars into a `float4` instead.

The engine has no opinion about what the bytes mean — `GltfMaterialFactors` in `Loom.Assets` is one possible shape, not a privileged one.

---

## 8. Entry points

Name them `vertexMain` / `fragmentMain` / `computeMain` and mark them with `[shader("vertex")]`, `[shader("fragment")]`, `[shader("compute")]`. The build detects the stages from those attributes; the SDK errors if a declared entry name isn't found.

A compute technique states its entry once via `TechniqueDescriptor.Compute("shader")`. What the compiled artifact ends up calling that entry internally is the backend's problem — Vulkan resolves the real SPIR-V entry from the module, so a toolchain that renames the sole entry to `main` is handled without the author knowing.

---

## 9. Where scalar parameters actually go

`ctx.FullScreen(technique, push, …)` and `ctx.Dispatch(technique, groups, push, …)` take a `ReadOnlySpan<float>`. Where it lands depends on what the technique declared:

| The shader declares | The span goes to | Cap |
|---|---|---|
| `LOOM_PUSH_CONSTANT` block (`HasFragmentPush: true`) | push constants / root constants at `b0` | **16 floats** |
| a descriptor-bound `ConstantBuffer<T>` instead | the per-draw uniform slot, via the per-frame ring | uncapped in practice (CSM pushes 68 floats) |

A technique has one or the other, so the same bytes are offered to both channels and the technique's binding model takes whichever it declares.

> **Known gaps ([#16](https://github.com/AtomicBlom/Loom/issues/16)):** pushing more than 16 floats is **silently truncated** on both backends, and on Vulkan pushing floats when the reflected uniform slots are already full is **silently dropped** where DirectX 12 throws. Both should be errors. Until then, if a push seems to have no effect, check the count and the slot budget first.
