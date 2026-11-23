# Shader authoring

The Slang-side contract: what a shader must declare so Kaldra's backends can bind it, and why the rules are what they are.

[`api-vocabulary.md`](api-vocabulary.md) documents the C# surface. This is the other half — the part written in `.slang` — and until now it lived only in backend source comments, which is a poor place for a contract app authors have to satisfy.

One source compiles to **SPIR-V** (Vulkan), **signed DXIL** (DirectX 12), and **WGSL** (WebGPU). The targets number their bindings differently, so a shader numbers none of them: Slang does, per target, and each backend binds by what that target's reflection says (§2). What the targets still disagree about is quarantined in one file — `BinaryVibrance.Kaldra.Shaders/Shaders/kaldra_platform.module.slang` — behind macros.

---

## 1. The platform macros

`#include "kaldra_platform.module.slang"` first, then use these instead of writing a target conditional yourself.

| Macro | Purpose |
|---|---|
| `KALDRA_PUSH_CONSTANT` | Marks the small inline scalar block. `[[vk::push_constant]]` for SPIR-V/DXIL; lowers to an ordinary `uniform` for WGSL, which has no push constants. |
| `KALDRA_FULLSCREEN_POS(uv)` | Clip position for a fullscreen triangle fabricated from `SV_VertexID`, given its 0..2 uv. |
| `KALDRA_CLIP_POS(xy)` | Clip position for geometry already authored in NDC (−1..1) — the same job as above for a shader that isn't fullscreen-shaped. |
| `KALDRA_OVERLAY_POS(px, scale, translate)` | Clip position for a 2D overlay/UI vertex in surface pixels. |
| `KALDRA_CLIP_TO_UV(clip)` | Screen uv (0..1, top-left origin) of a clip-space position, for reprojecting a world point to sample a G-buffer there. |

The four position macros all exist for one reason: **NDC-Y**. Vulkan and DirectX 12 present a Y-down NDC (DX12 via a negative-height viewport), WebGPU a Y-up one with no negative viewport. A shader that goes through the camera never notices, because `Camera.ViewProjection` composes `IEngineClipSpace.ClipSpaceCorrection`. A shader that writes clip space *directly* has no camera to carry that correction, so the convention has to be applied at the point of emission — which is what these macros do.

> **Invariant:** `kaldra_platform.module.slang` is the only shader in the repository containing an `#ifdef KALDRA_WGSL`. If you find yourself writing one, the macro you need is missing — add it there rather than branching in place. `grep -rl KALDRA_WGSL --include=*.slang` should return exactly that one file.

---

## 2. Declaration order is the only numbering

A shader declares its resources and gives them no numbers:

```hlsl
Sampler2D albedoTex;
ConstantBuffer<ViewProj> view;
ConstantBuffer<PerDraw>  draw;
```

Slang numbers them for each target in declaration order: SPIR-V bindings in one list, D3D registers per kind (`t`, `s`, `b` and `u` counted apart, a `Sampler2D` taking a `t` and an `s`), WGSL bindings in one list (a `Sampler2D` taking two). Every stage of a module gets the same numbers. The build writes each target's numbers to its own reflection sidecar, and each backend binds by its own:

| Target | Sidecar | The backend binds |
|---|---|---|
| SPIR-V | `<name>.json` | each descriptor at its reflected binding (§3) |
| DXIL | `<name>.dxil.json` | each root parameter at its reflected register (§4) |
| WGSL | `<name>.wgsl.json` | each bind-group entry at its reflected binding (§6) |

What a shader does state is **order**. A pass's inputs fill a technique's slots kind by kind in declaration order: its `k`-th sampled input goes to the `k`-th `Sampler2D` declared, its `k`-th storage buffer to the `k`-th buffer, and its uniform slots are filled as §5 says. So a module that every lit shader includes declares its resources before the shader's own, and they come first in every pass that draws with it.

Don't write `[[vk::binding]]` or `: register(...)` yourself. They buy nothing, since every backend binds by whatever its reflection reports, and a register survives into the WGSL compile, where it becomes that parameter's WGSL binding (warning 39029) and registers of different kinds (`b1` and `t1`) collide.

---

## 3. Vulkan: one descriptor set, at reflected bindings

Everything lands in **set 0**, and `VulkanTechnique.CreateSetLayout` builds one entry per parameter **at the binding reflection gives it**, with the descriptor type its reflected shape implies and a descriptor count from its reflected slot span.

**No two parameters may claim the same slot** — an arrayed binding claims all of its. `ShaderLayoutParityTests` asserts it over every shipped sidecar, so a collision fails the suite rather than the picture.

---

## 4. DirectX 12: a root signature at reflected registers

The root signature is, in order: an optional 32-bit root-constants block, then one root CBV per reflected constant buffer, then an optional SRV descriptor table with a static sampler per sampled input, then any storage-buffer root SRVs (a compute technique's read-write storage goes in a UAV table, or a root UAV for a raw buffer). Each is built at the register the DXIL sidecar gives its parameter (`DirectX12Registers`), one descriptor range per texture, so the registers need not be contiguous.

The root-constants block is the shader's `KALDRA_PUSH_CONSTANT` block, which D3D lays out as an ordinary constant buffer in declaration order: the backend finds it as the one constant buffer the SPIR-V sidecar doesn't list as a descriptor. A technique with root constants and no push block puts them after the shader's last constant buffer, where they overlap nothing.

---

## 5. Uniform slots are filled in a defined order

A technique's uniform buffers are filled, slot 0 upward:

1. the slot reflection identifies as the **per-material parameter block**, if the shader declares one (§7)
2. then the pass's `.Reads(...)` constants, **in declaration order**
3. then the per-draw block (`DrawCall.Uniforms` / `SetUniforms`)

So a pass declaring `.Reads(viewProj)` and a draw supplying a per-draw block gives the shader two constant buffers, view-projection first. Adding a second `.Reads` inserts it before the per-draw block — the per-draw block is always last.

A compute dispatch fills its constant buffers from the pass's `.Reads(...)` alone, in declaration order: it has no per-draw block, and its push rides only the scalar block (§9), never a uniform slot. On DirectX 12 they are root CBVs as a draw's are. A compute technique that declares more constant buffers than its pass reads throws at dispatch on every backend, rather than dispatching with a slot unbound. The Particles demo's fountain (`particle_fountain.slang`) reads its shape this way.

A compute technique reads app-written storage the same way: each read-only `StructuredBuffer<T>` it declares takes the pass's `.Reads(storage)` in declaration order, while each `RWStructuredBuffer<T>` takes the dispatch's own storage. Declare the read-write buffers first and the read-only after them. A compute technique declaring more read-only buffers than its pass reads throws at dispatch on every backend, as does a dispatch supplying a different number of read-write buffers, or of storage images, than the technique declares: a surplus would be bound nowhere, a shortfall would leave a slot unbound. The Storage Scatter demo (`storage_scatter.slang`) expands a density map written once into a field of sparks this way.

A read-only `StructuredBuffer<T>` in a compute technique binds **only** the pass's `.Reads` storage (`g.Storage`). A buffer the GPU produced (`g.Buffer`, written by an earlier dispatch) reaches a dispatch only through its own storage list, which fills the `RWStructuredBuffer<T>` slots, so declare it `RWStructuredBuffer<T>` even where the shader only reads it.

The binding is **positional, never by name**, for draws and dispatches alike: a technique's `k`-th storage buffer (in binding order; a compute technique's `k`-th read-only one) takes the `k`-th storage buffer its pass `.Reads`. A technique that reads only some of a pass's buffers must declare them in the order the pass reads them, from its first: one declaring only the pass's second gets its first. Each slot's reflected element size is held to the size of the buffer it lands on (the `T` of `g.Storage<T>`, packed or padded to the element's std430 alignment), and a mismatch throws at the draw or dispatch naming both, which catches that mistake wherever the two element types differ in size.

---

## 6. WebGPU: bind groups from the WGSL

Slang assigns WGSL bindings in declaration order, a combined `Sampler2D` taking **two** (texture + sampler), and the WebGPU backend recovers the real assignments from the emitted WGSL and its sidecar.

The three sidecars are *different index spaces for the same source*. A `ShaderReflection` records which one it holds, and building a Vulkan or DirectX 12 layout from a WGSL reflection throws rather than producing a layout that looks plausible and binds the wrong things.

Consequences worth knowing: WGSL has no push constants (`KALDRA_PUSH_CONSTANT` becomes a uniform), and vertex-input locations may be permuted relative to stream order, which is why the backend parses them rather than assuming.

---

## 7. The per-material parameter block

A shader opts into per-material parameters by declaring a constant buffer under the reserved name **`kaldraMaterial`**:

```hlsl
struct KaldraMaterial
{
    float4 baseColor;
    float4 emissive;
    float4 metallicRoughness;
};

ConstantBuffer<KaldraMaterial> kaldraMaterial;
```

Reflection reports which uniform slot it is and how many bytes it occupies; the engine stages a material's bytes into that slot on every draw using it. Convention is to declare it last, but nothing depends on that — it binds at whatever slot reflection reports.

**Lay the struct out in 16-byte-aligned rows.** HLSL constant-buffer packing and WGSL uniform layout agree on `float4` and disagree on tighter shapes, so a bare `float3` or a trailing `float` lands at different offsets on different backends. Pack loose scalars into a `float4` instead.

The engine has no opinion about what the bytes mean — `GltfMaterialFactors` in `BinaryVibrance.Kaldra.Assets` is one possible shape, not a privileged one.

---

## 8. Entry points

Name them `vertexMain` / `fragmentMain` / `computeMain` and mark them with `[shader("vertex")]`, `[shader("fragment")]`, `[shader("compute")]`. The build detects the stages from those attributes; the SDK errors if a declared entry name isn't found.

A compute technique states its entry once via `TechniqueDescriptor.Compute("shader")`. What the compiled artifact ends up calling that entry internally is the backend's problem — Vulkan resolves the real SPIR-V entry from the module, so a toolchain that renames the sole entry to `main` is handled without the author knowing.

**Variants.** One source compiled again with macros defined, under a name of its own, is a `KaldraSlangVariant`, not a second file that defines the macro and includes the first:

```xml
<ItemGroup>
  <KaldraSlangVariant Include="Shaders\terrain.slang;Shaders\car.slang" Suffix="_cast" Defines="CAST" />
</ItemGroup>
```

That makes `terrain_cast` and `car_cast` beside `terrain` and `car`, on every backend, each a shader of its own that a `TechniqueDescriptor` names (`Defines` takes a list, `A;B=2`). The stages are found by scanning the source, as an entry's are, and the scan doesn't see `#ifdef`: a variant that compiles a stage out names the stages it keeps with `Stages="vertex"` on the item. Declare variants before the SDK's targets are imported, so the IDE's up-to-date check sees their outputs.

---

## 9. Where scalar parameters actually go

`ctx.FullScreen(technique, push, …)` and `ctx.Dispatch(technique, groups, push, …)` take a `ReadOnlySpan<float>`. Where it lands depends on what the technique declared:

| The shader declares | The span goes to | Bound |
|---|---|---|
| `KALDRA_PUSH_CONSTANT` block (`HasFragmentPush: true`) | push constants / root constants | **16 floats** — the block's fixed size |
| a descriptor-bound `ConstantBuffer<T>` instead | the next free uniform slot, via the per-frame ring | **that buffer's declared size** (CSM's is 68 floats wide, and it pushes 68) |

A technique has one or the other, so the same bytes are offered to both channels and the technique's binding model takes whichever it declares.

The uniform route is not uncapped: it is a real buffer, but a buffer of the size the shader declared, and reflection reports that size. Pushing more than the target holds is an error, on either route — as is pushing when the technique declares no block *and* the pass's `.Reads` have already filled every constant buffer it reflects, which leaves the bytes nowhere to go. All three cases used to be silent (truncated, or dropped entirely); they now throw `TechniqueMismatchException` naming the pass, the technique, and both counts.

One thing the check cannot do: a constant buffer's reflected size includes padding to its 16-byte rows, so a push that *fits* is not thereby laid out the way the shader reads it. Getting the size right does not excuse §7's advice about packing loose scalars into a `float4`.
