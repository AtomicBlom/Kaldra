# Loom — Code Review Findings

**Date:** 2026-06-20
**Method:** Exhaustive multi-agent audit — 17 reviewers (per-project + cross-cutting dimensions) fanned out, every finding was re-checked by an independent adversarial verifier that re-opened the cited code, then a synthesis pass deduped and ranked. 101 raw findings checked, **96 confirmed, 5 rejected** as false positives.
**Scope:** all 10 projects. M15 (game-archetype track) and MU1 (UI) are being written by other agents in parallel — findings in that territory are flagged as likely work-in-progress.
**Axes:** architectural drift · correctness · cross-backend parity · maintainability · forward-looking dead-ends (backends + advanced GPU features) · half-baked enums.

---

## Final-state reconciliation (updated post M15 + UI font rendering)

> This review was written against commit `6631217`. **Milestone 15** (skinning/CSM/sky/foliage/toon) and the **MU1 UI font-rendering** milestone have since landed (`Review` now tracks `master`). Status of the findings against the current tree:
>
> - **Most findings STAND** — the landed work added features, it did not fix the flagged defects. All P0 correctness bugs below are unchanged.
> - **Resolved / partial:** `VertexFormat` gained `UInt4` (skinning need met; enum still narrow — *partial*); glTF skeleton/animation is correct but node-transform placement still STANDS for static/multi-node; the game-UI overlay seam is now exercised by several demos (*resolved*); cross-backend parity-test infrastructure exists but the direct Vulkan-vs-DirectX12 assertion still does not (*partial*).
> - **Font rendering now draws at cross-backend parity**, which resolved the "no-op UI renderer" comments — but left `TextBlock`'s `Text` doc stale-and-wrong (now **fixed**). No major new defects in the M15/font code.
> - **All 7 XAML-generator defects and all 5 UI layout/DP findings STAND.**
>
> Remediation is sequenced in the approved plan (`right-sorry-i-just-serialized-octopus.md`). This document's per-finding detail below remains accurate except where noted above.

---

## Executive summary

Loom is **architecturally healthy at its core.** The cardinal invariant — no graphics-API type in `Loom.Core`/`Loom.Graph` — was independently verified clean, the Describe/Execute/Backend separation holds, and several forward-looking seams are genuinely well-designed (see *Positive confirmations* at the end): the neutral RH/+Y-up matrix convention with per-backend clip fix-up, the *advisory* (non-mandatory) `BarrierPlan`/`AliasPlan`, derived per-attachment Load/Store actions, the opaque `object surface` creation seam, and the preserved dependency DAG in the Execute layer.

**Risk concentrates in three places:**

1. **A cluster of latent correctness bugs** masked by current demo usage but which will fire as the engine scales: the DirectX12 descriptor-ring mid-frame wrap can silently corrupt per-pass descriptors, sRGB mips are box-filtered in gamma space (visibly wrong albedo on *both* backends), the bake-cache hash omits buffer `ByteSize`, and glTF loading ignores node transforms entirely.
2. **The async-compute / multi-queue façade** — `Queue.AsyncCompute` and `.Queue()` are authorable and documented as honored, but no layer derives cross-queue sync and neither backend creates a compute queue. The API actively lies about a named forward-looking pillar.
3. **The brand-new `Loom.UI.SourceGen` XAML generator** (MU0/MU1, in flight) has several real correctness defects — broken incrementality, hint-name collisions, and unvalidated input that surfaces as confusing generated-code compile errors.

**Cross-backend parity — the headline invariant — has no automated asserting test**, despite the comparison infrastructure already existing.

The **forward-looking gaps** (ray tracing, bindless, GPU-driven indirect, temporal history) are mostly *honest, additive friction rather than irreversible dead-ends* — helped by backend isolation and switch statements that throw loudly on unhandled members. The exceptions worth fixing *now* are the over-promising APIs: the async-compute façade, and the persistent history-ring `Count` field that is hashed-but-never-allocated.

### Counts by severity

| Severity | Count | Meaning |
|---|---|---|
| 🔴 Critical | 0 | breaks correctness/portability now, or irreversible future dead-end |
| 🟠 Major | 11 | real bug / serious drift / significant friction |
| 🟡 Minor | ~33 | quality / maintainability / latent traps |
| ⚪ Note | ~25 | nits, observations, + 7 *positive* confirmations |

---

## Top priorities (fix first)

1. **🟠 DirectX12 descriptor-ring mid-frame wrap** — [Loom.DirectX12/DirectX12DescriptorRing.cs:59-67](Loom.DirectX12/DirectX12DescriptorRing.cs#L59). Replace the silent `_next = _regionEnd - _regionSize` wrap-into-a-live-region with a throw/grow. It can silently corrupt descriptors an already-recorded draw still references → wrong pixels, no error. The M13 resource viewer will erode the headroom further.
2. **🟠 Gamma-incorrect sRGB mip generation** — [Loom.Core/TextureData.cs:69-99](Loom.Core/TextureData.cs#L69). Box-average sRGB albedo in *linear* space (decode → average → re-encode). Every base-colour/glTF texture currently gets systematically-too-dark mips on **both** backends. Parity is preserved but the answer is wrong.
3. **🟠 Resolve the async-compute façade** — [Loom.Core/Queue.cs](Loom.Core/Queue.cs) + `GraphSchedule` + both backends. Either implement the cross-queue derivation or soften the doc to *"accepted but not yet honoured; treated as Graphics"* and reject `Queue.AsyncCompute` at derive time so the API stops lying.
4. **🟠 Bake-cache hash omits buffer `ByteSize`** — [Loom.Core/RenderGraph.cs:85-87](Loom.Core/RenderGraph.cs#L85). Two graphs differing only in a buffer's byte size hash identically → cache reuses a stale-sized GPU allocation. `ToJson` already serializes `ByteSize`, so the two structural views disagree.
5. **🟠 `Loom.UI.SourceGen` correctness defects** — [XamlSourceGenerator.cs](Loom.UI.SourceGen/XamlSourceGenerator.cs). Broken incrementality, short-class-name hint collisions, and unvalidated `x:Name`/literal/enum values that emit uncompilable code with no diagnostic. *(MU1 in flight — confirm with the owning agent first.)*
6. **🟠 glTF node-transform omission** — [Loom.Assets/GltfLoader.cs:26-27](Loom.Assets/GltfLoader.cs#L26). The loader iterates `LogicalMeshes` directly, ignoring the scene graph → non-trivial glTF loads scrambled at the origin; multi-instanced meshes load once.
7. **🟠 DXIL build passes green on DX-only shader failures** — [build/Loom.Sdk/Loom.Shaders.targets:71-73](build/Loom.Sdk/Loom.Shaders.targets#L71). `|| exit /b 0` unconditionally swallows *every* DXC error → a SPIR-V-valid/DXIL-invalid shader ships no blob and DirectX12 silently falls back at runtime. The exact class of parity break this layer exists to prevent.
8. **🟡 Add an automated cross-backend parity test** — the parity bar (4/255, <0.5%) and `ImageComparison` comparator already exist; no test ever diffs a Vulkan capture against a DirectX12 capture.

---

## Forward-looking risks (named-pillar dead-end analysis)

> Severity model: irreversible / painful-rewrite = Major; mere future friction = Minor/Note. **None of the named pillars is irreversibly foreclosed** — but two APIs over-promise today.

- **Async compute / multi-queue is a *façade*, not an accommodation.** `Queue`/`PassBuilder.Queue`/`GraphPass.Queue` exist and are documented as honored, but `GraphSchedule.Derive` and `BarrierPlan.Derive` never read `pass.Queue` (only `Dump`/`ToDot`/`Hash` labels do), the scheduler is single-timeline with one `ResourceState` per resource, and both backends create exactly one queue. The *describe/execute seam* (DAG + `.Queue` verb) already exists; the gap is the derivation + backend queues. **Recoverable, but the API lies today.**
- **Hardware ray tracing cannot be expressed at all** (additive friction, not a dead-end). No acceleration-structure `ResourceKind`, no RT/AS-build `PassKind`, no build/read-AS `AccessKind` edge, no `AccelerationStructure` `ResourceState`. The central derivation switches + `IRenderContext` all need new arms (`ctx.TraceRays`/`ctx.BuildAccelerationStructure`). The throwing state switches will surface the gaps loudly.
- **Bindless / descriptor-indexing is foreclosed without backend rework** (friction, lives in isolated backends). Reflection collapses resources to fixed counts, `Technique` builds one set with `DescriptorCount=1` per binding, flat `t0..tN/s0..sN/u0`, no `PartiallyBound`/`VariableDescriptorCount`/`UpdateAfterBind`, no unbounded array. The authoring API binds positionally by `.Samples` ordinal — the opposite shape of a global bindless heap.
- **GPU-driven indirect / mesh shaders:** every draw verb takes a CPU-supplied count (including the GPU-buffer overload `DrawInstanced(...,BufferHandle,int count)`). No `DrawIndirect`/`DrawIndirectCount` verb, so a GPU culling pass can't determine surviving counts; `PassKind` has no mesh/amplification dispatch. The compute→draw barrier edge already exists, so recoverable.
- **Temporal techniques (TAA/upscaling, DDGI/GI history):** `GraphBuilder.Persistent(name,format,size)` documents `size` as the history-ring depth and hashes it into `ResourceHandle.Count`, but both backends allocate exactly **one** physical image and never read `Count` — a declared `size:2` silently yields a single aliased image + read-after-write hazard. There is also no `ctx.PreviousCamera` to reproject *from*, and `Domain` has no array-layer/view-count axis for VR multiview or a motion-vector concept. All additive, but the API over-promises.
- **WebGPU friction (not a dead-end):** the storage model collapses every storage access to `ResourceState.Storage` = GENERAL read-modify-write with no read-only/write-only distinction. WebGPU core storage *textures* are write-only by default, so a read-modify-write storage-image technique (auto-exposure) may not compile on a WebGPU backend without remapping. Texture/geometry streaming has no async/partial-residency seam — loaders are fully synchronous and fully resident.

---

## Findings by theme

### 1 · Correctness & resource-safety bugs

Latent defects masked by current demo usage or single-backend conventions, but which will fire as the engine scales.

- **🟠 DirectX12 descriptor-ring mid-frame wrap can clobber descriptors a still-recording frame references** — [DirectX12DescriptorRing.cs:59-67](Loom.DirectX12/DirectX12DescriptorRing.cs#L59) · also [DirectX12Engine.cs:127](Loom.DirectX12/DirectX12Engine.cs#L127), [DirectX12RenderContext.cs:133](Loom.DirectX12/DirectX12RenderContext.cs#L133), [DirectX12ImGuiRenderer.cs:529](Loom.DirectX12/DirectX12ImGuiRenderer.cs#L529) · *correctness, conf: medium*
  `Allocate()` does `if (_next + count > _regionEnd) _next = _regionEnd - _regionSize;` — resetting the cursor to the **start** of this slot's live region, not a free area. D3D12 reads descriptors at execute time, so a later pass overwrites descriptors an earlier draw in the same command list still references. No assert on overflow.
  *Fix:* throw (or grow the heap) on overflow instead of wrapping; size from a documented worst-case per-frame descriptor budget, or track a high-water mark.

- **🟠 sRGB mip generation box-averages in gamma space, not linear** — [TextureData.cs:69-99](Loom.Core/TextureData.cs#L69) · also [TextureData.cs:43-63](Loom.Core/TextureData.cs#L43), [GltfLoader.cs:88](Loom.Assets/GltfLoader.cs#L88) · *correctness, conf: high*
  `Downsample` averages raw stored bytes for all channels/formats; `Format` is on the instance but never threaded in. `Rgba8Srgb` texels are sRGB-encoded, so averaging the encoded bytes is gamma-incorrect — downsampled levels are systematically too dark. (Normal maps have a lesser issue: averaging then not renormalising shortens the normal.)
  *Fix:* branch `Downsample` on `Format`: for `Rgba8Srgb` decode → average in linear → re-encode (use a precomputed 256-entry table for determinism); keep the integer path for `Rgba8Unorm`.

- **🟠 glTF loader ignores node transforms and scene graph** — [GltfLoader.cs:26-27](Loom.Assets/GltfLoader.cs#L26) · also [GltfLoader.cs:34-49](Loom.Assets/GltfLoader.cs#L34), `ModelPrimitive.cs` (no Transform field) · *correctness, conf: high*
  `foreach (mesh in model.LogicalMeshes)` iterates the mesh table directly — never walks `DefaultScene`/`LogicalNodes`, never reads `WorldMatrix`. Every mesh collapses to local space at the origin; an N-node-referenced mesh renders once. Non-uniform scale also needs inverse-transpose normals. The single committed cube masks it.
  *Fix:* walk `DefaultScene.VisualChildren` (or nodes where `node.Mesh != null`) and bake `WorldMatrix` into positions (inverse-transpose for normals/tangents), or add `Matrix4x4 Transform` to `ModelPrimitive` and emit one primitive per (node, mesh-primitive).

- **🟠 `RenderGraph.Hash()` omits buffer `ByteSize` → bake-cache collision** — [RenderGraph.cs:85-87](Loom.Core/RenderGraph.cs#L85) · also [RenderGraph.cs:246](Loom.Core/RenderGraph.cs#L246) (ToJson *does* serialize it) · *correctness, conf: high*
  The per-resource hash line lacks `ByteSize`, the sole structural differentiator for buffer resources. Two graphs differing only in a buffer's size (e.g. `1<<16` vs `1<<20`) hash identically → a cache reuses the stale-sized plan.
  *Fix:* append `|b={resource.ByteSize}` to the hash line, matching the `ResourceDto`.

- **🟡 `NativeArena.Alloc` size is a 32-bit multiply that can overflow** — [NativeArena.cs:21-26](Loom.Vulkan/NativeArena.cs#L21) · *correctness, conf: medium*
  `(nuint)(sizeof(T) * count)` computes `int * int` in 32-bit *before* the cast → under-sized buffer → heap corruption. Not hit today (only small CreateInfo trees), but a latent overflow primitive for any future large array (bindless descriptors).
  *Fix:* `(nuint)sizeof(T) * (nuint)count`, reject negative count.

- **🟡 `UsesOf` collapses same-pass Sample+Write of one handle to the write state** — [BarrierPlan.cs:148-163](Loom.Graph/BarrierPlan.cs#L148) · *correctness, conf: medium*
  Reduces all accesses of a resource in a pass to one `Use` by fixed priority, discarding the Sample (and losing blend semantics). `ColorHandle` satisfies both `.Samples` and `.Writes`, so the combination is constructible with no guard. No shipped demo does it, but it silently mis-derives the barrier for any future feedback/programmable-blend technique on a single handle.
  *Fix:* reject the incompatible combination at derive time, or model it as an explicit input-attachment/feedback state.

- **🟡 `AssetWatcher` can fire `Changed` after `Dispose`** — [AssetWatcher.cs:35,63-85](Loom.Assets/AssetWatcher.cs#L35) · *correctness, conf: medium*
  `Timer.Dispose()` (parameterless) doesn't block an in-flight callback; a `Flush()` already running can invoke `Changed?.Invoke` after `Dispose` returns. No `_disposed` flag checked in `Flush`. Dev-only hot-reload, but a real teardown race.
  *Fix:* volatile `_disposed` flag set under `_gate`, checked atop `Flush`; or `Timer.Dispose(WaitHandle)`/`DisposeAsync` to drain.

- **🟡 `VulkanContext` ctor leaks instance/surface/device on mid-bring-up failure** — [VulkanContext.cs:57-83](Loom.Vulkan/VulkanContext.cs#L57) · *maintainability, conf: high*
  The ctor runs CreateInstance → CreateSurface → PickPhysicalDevice → CreateLogicalDevice with no try/catch; if a later step throws, `Dispose()` never runs and live handles leak. Bounded today (process exits), but not exception-safe for any future retry (multi-GPU fallback, headless probing).
  *Fix:* wrap bring-up in try/catch that `Dispose()`s before rethrowing, or use a disposing factory.

- **🟡 `LayeredAssetSource.TryResolve` does no path-traversal containment** — [LayeredAssetSource.cs:19-32](Loom.Assets/LayeredAssetSource.cs#L19) · *correctness, conf: low*
  `Path.Combine(root, logicalName)` with no check the result stays under `root`; `..\..\secret` or an absolute name escapes. Currently unused in production paths (names are leaf-relative), but the contract doesn't constrain it — a future resource-pack/user-content path could traverse out and defeat layering.
  *Fix:* reject rooted names; verify `GetFullPath(candidate)` starts with `root + separator`.

- **⚪ `RecordRasterPass` writes fixed-size `stackalloc[8]` with no bound on `colorCount`** — [DirectX12Engine.cs:388-421](Loom.DirectX12/DirectX12Engine.cs#L388) · *correctness, conf: high*
  D3D12 caps MRT at 8 so a conforming graph stays in bounds, but a 9+ colour-write pass would stack-corrupt rather than fail clearly. No upstream guard in `Loom.Graph`/`PassBuilder`.
  *Fix:* `if (colorCount >= 8) throw …` (constant matching `D3D12_SIMULTANEOUS_RENDER_TARGET_COUNT`).

- **⚪ `ImageLoader` surfaces low-context exceptions with no path / null-decode guard** — [ImageLoader.cs:19-30](Loom.Assets/ImageLoader.cs#L19) · *correctness, conf: medium*
  Missing file throws raw `FileNotFoundException`; a null/zero-dimension decode triggers `FromRgba8`'s RGBA-byte-count error rather than "failed to decode `<path>`". `GltfLoader.DecodeChannel` already guards; the standalone path doesn't.
  *Fix:* wrap decode failures in an asset-loader exception naming the path; null/zero-dimension guard before `FromRgba8`.

### 2 · Async-compute / multi-queue façade

The single most-reported issue (5 reviewers). Consolidated.

- **🟠 `Queue.AsyncCompute` is declarable and documented as honored, but Execute + both backends silently ignore it** — [Queue.cs:7-14](Loom.Core/Queue.cs#L7) · [PassBuilder.cs:64-69](Loom.Core/PassBuilder.cs#L64) · [GraphSchedule.cs:47-186](Loom.Graph/GraphSchedule.cs#L47) · [BarrierPlan.cs:41-108](Loom.Graph/BarrierPlan.cs#L41) · [VulkanContext.cs:375-435](Loom.Vulkan/VulkanContext.cs#L375) · [DirectX12Context.cs:70-77](Loom.DirectX12/DirectX12Context.cs#L70) · *future-proofing, conf: high*
  `Queue.cs` documents *"the graph derives the cross-queue timeline synchronisation."* But `Derive`/`TopologicalOrder`/`BarrierPlan.Derive` never read `pass.Queue` (only label strings do); the scheduler is single-timeline; Vulkan acquires only Graphics+Present (no compute family); DirectX12 creates one Direct queue + one fence. A user writing `.Queue(Queue.AsyncCompute)` gets it scheduled exactly as Graphics — a false affordance and a misleading present-tense doc. Planning.md §12 places this under M9 (partial), so the gap is *deferred*, not accidental — but the doc reads as shipped.
  *Fix:* (a) remove the member until wired, or (b) keep it but make the doc explicit and **reject `AsyncCompute` at derive time** (fail-loud). When implemented: partition `GraphSchedule` per queue, emit cross-queue edges, extend `BarrierPlan`/`ResourceTransition` with queue-ownership/semaphores, acquire a compute queue. Consider adding a `Transfer` member at the same time.

### 3 · Cross-backend parity risks

Points where the two backends derive or apply something on different principles — latent traps masked by current usage.

- **🟠 DXIL compile errors silently swallowed by `|| exit /b 0`** — [Loom.Shaders.targets:71-73](build/Loom.Sdk/Loom.Shaders.targets#L71) · also [DirectX12Engine.cs:889-906](Loom.DirectX12/DirectX12Engine.cs#L889) (runtime fallback) · *tests/parity, conf: high*
  The swallow is *unconditional* — it can't distinguish "entry not found" (expected) from "entry found but failed DXIL compile" (a real DX-only divergence). Such a shader ships no blob; at runtime DirectX12 substitutes a stand-in (visual divergence) or throws only when the demo runs that technique. Hides the most likely class of cross-backend break.
  *Fix:* probe per-shader which stages should exist and hard-fail those; or post-validate every entry produced its declared `.dxil`. At minimum distinguish exit codes.

- **🟡 Vulkan-vs-DirectX12 parity is never exercised by any test** — [ImageComparison.cs:3-4](Loom.Tests/ImageComparison.cs#L3) · also [GoldenImageTests.cs:15-16](Loom.Tests/GoldenImageTests.cs#L15), [tools/golden.ps1:45-50](tools/golden.ps1#L45) · *parity, conf: high*
  `ImageComparison` documents a "cross-backend check (Vulkan vs DirectX12)" but golden tests compare each backend against committed baselines *independently* — never a Vulkan capture against a DirectX12 capture. The headline invariant has no automated assert; the `Max/MeanChannelDiff` the bar needs is computed but unwired.
  *Fix:* an opt-in, GPU-gated test that reads a Vulkan + DirectX12 capture of the same demo and asserts `FailedFraction < 0.005` at tolerance 4.

- **🟡 DirectX12 PSO cache key omits template/bytecode content** — [DirectX12TechniqueCache.cs:34,48-55](Loom.DirectX12/DirectX12TechniqueCache.cs#L34) · contrast [TechniqueCache.cs:33-57](Loom.Vulkan/TechniqueCache.cs#L33) · *parity, conf: high*
  DirectX12 `BuildKey` hashes only name + formats + depth flags; the template (bytecode, blend, layout, instance) is **not** keyed. Vulkan hashes the SPIR-V + all state. So a `Register(name, newTemplate)` re-bakes on Vulkan but returns the stale PSO on DirectX12. Masked today (hot-reload calls `Reset()` wholesale), but a latent divergence the moment a name is re-registered with new content.
  *Fix:* fold a hash of the template's bytecode + blend/layout/instance into the DirectX12 key, mirroring Vulkan.

- **🟡 Compute sampled-input filtering diverges: Vulkan hardcodes nearest, DirectX12 honors `LinearFilter`** — [RenderContext.cs:280](Loom.Vulkan/RenderContext.cs#L280) (vs [:397-409](Loom.Vulkan/RenderContext.cs#L397) which does it right) · [DirectX12Technique.cs:117](Loom.DirectX12/DirectX12Technique.cs#L117) · *parity, conf: high*
  Vulkan's `Dispatch` binds a sampled input with a fixed nearest sampler, ignoring the technique's `LinearFilter`; DirectX12 bakes a static sampler from the flag. A `LinearFilter:true` compute technique would sample nearest on Vulkan, linear on DirectX12. Masked because the only shipped sampling-compute ("exposure") uses `LinearFilter:false`.
  *Fix:* select Vulkan's `Dispatch` sampler from `LinearFilter` exactly as `BindSampledInputs` does.

- **🟡 Instance-from-buffer vertex stride differs between backends** — [DirectX12RenderContext.cs:422-444](Loom.DirectX12/DirectX12RenderContext.cs#L422) · vs [RenderContext.cs:335-344](Loom.Vulkan/RenderContext.cs#L335) · *parity, conf: low*
  DirectX12 computes `StrideInBytes = byteSize / count`; Vulkan uses the pipeline's declared instance stride. If `byteSize/count != declared stride` (padding, or over-allocated buffer), the backends fetch per-instance attributes from different offsets. Unused by shipped demos (M15 GPU-driven territory).
  *Fix:* DirectX12 should take the stride from `template.Instance.Stride`, matching Vulkan's source of truth.

- **⚪ Game-UI overlay seam exercised by only one demo despite a host comment implying all** — [CubeLightingDemo.cs:33](Loom.Sandbox/Demos/CubeLightingDemo.cs#L33) · [Program.cs:97-100](Loom.Sandbox/Program.cs#L97) · *parity, conf: medium*
  `DemoGraph.UI(...)` is called by exactly one demo; the MU0 "ui" pass + `ctx.DrawUI()` path is validated only on the simplest graph. Low risk (no-op renderer this phase, MU1 in flight).
  *Fix:* add `DemoGraph.UI` to other graphs as the renderer becomes non-trivial, or narrow the comment.

### 4 · `Loom.UI.SourceGen` (XAML generator) — MU0/MU1 in flight

Real correctness defects in the Roslyn generator. MU1 is being written by another agent — **confirm before acting** — but these are shipped MU0 defects, not obviously half-finished stubs.

- **🟠 Incremental pipeline combines the raw `Compilation`, defeating caching** — [XamlSourceGenerator.cs:40-42](Loom.UI.SourceGen/XamlSourceGenerator.cs#L40) · also [:55-101,161-182](Loom.UI.SourceGen/XamlSourceGenerator.cs#L55) · *architecture, conf: high*
  `xaml.Combine(context.CompilationProvider)` feeds the `(AdditionalText, Compilation)` tuple straight to `RegisterSourceOutput` with no value-equatable model. `Compilation` has no value equality → considered changed on *every* compilation → full `XDocument.Parse` + recursive semantic walk re-runs on every keystroke in *any* C# file. The canonical "incremental in name only" anti-pattern.
  *Fix:* project `AdditionalTextsProvider` → a value-equatable model (path + text + parsed shape) with `.Select`, and only `Combine` with a narrowed provider at the final resolution stage.

- **🟠 `AddSource` hint name uses the short class name → namespace collision breaks the build** — [XamlSourceGenerator.cs:72,100](Loom.UI.SourceGen/XamlSourceGenerator.cs#L72) · *correctness, conf: high*
  `spc.AddSource($"{cls}.g.cs", …)` uses only the namespace-stripped name. Two views `A.MainView` and `B.MainView` both emit `MainView.g.cs` → Roslyn throws `ArgumentException` (hintName must be unique) and the whole compilation fails with no diagnostic.
  *Fix:* use the fully-qualified class as the hint name, sanitizing disallowed characters.

- **🟠 `x:Name` / generated variable names emitted verbatim with no validation or collision guard** — [XamlSourceGenerator.cs:91,110-121](Loom.UI.SourceGen/XamlSourceGenerator.cs#L91) · *correctness, conf: high*
  Raw `x:Name` is used directly as a C# identifier; `Root` field is emitted unconditionally. `x:Name='event'` (keyword) → uncompilable code; `x:Name='Root'` → duplicate field; `x:Name='__e0'` → collides with synthesized locals. All surface as confusing errors in a file the author can't edit.
  *Fix:* validate with `SyntaxFacts.IsValidIdentifier` + diagnostic; reserve/escape synthesized names; rename or diagnose `Root`.

- **🟠 Literal/enum conversion emits unvalidated text** — [XamlSourceGenerator.cs:184-197,134-136](Loom.UI.SourceGen/XamlSourceGenerator.cs#L184) · *correctness, conf: high*
  `Orientation='Sideways'` → `Orientation.Sideways` (CS0117); bool `'yes'` → `yes`; `FontSize='big'` → `bigf`. The call site guards only a null return (unsupported *type*), not a bad *value*. Each becomes a compiler error inside generated code.
  *Fix:* validate before emitting — check enum members exist (case-insensitive, support flags); `TryParse` bool/numeric and emit canonical literals or report a diagnostic.

- **🟡 Element XML namespace ignored during type resolution** — [XamlSourceGenerator.cs:104-107,160-161](Loom.UI.SourceGen/XamlSourceGenerator.cs#L104) · *correctness, conf: medium*
  Only `LocalName` is used; `el.Name.Namespace` is never consulted, so a typo'd/wrong xmlns still resolves and there's no xmlns→CLR-namespace mapping. Acceptable for MU0 but forecloses a clean multi-namespace story and fails silently.
  *Fix:* gate resolution on the namespace; plan an xmlns→CLR map; report `UnknownElement` on unrecognized namespace.

- **🟡 No cancellation checks inside the recursive emit walk** — [XamlSourceGenerator.cs:55-158](Loom.UI.SourceGen/XamlSourceGenerator.cs#L55) · *correctness, conf: medium*
  `CancellationToken` read once; `EmitElement` recurses + semantic lookups with no `ThrowIfCancellationRequested`. A cancelled IDE pass keeps working. Compounds the incremental-caching problem.
  *Fix:* thread `spc.CancellationToken` into `EmitElement`.

- **🟡 Text content and property-element / attached-property syntax silently dropped** — [XamlSourceGenerator.cs:129-130,141-144](Loom.UI.SourceGen/XamlSourceGenerator.cs#L129) · *correctness, conf: low*
  `<TextBlock>OK</TextBlock>` inner text, `Grid.Row`, property-element syntax all vanish with no `ReportDiagnostic` (6 descriptors exist; none covers these). Defensible MU0 scoping, but silent.
  *Fix:* emit an informational "not yet supported" diagnostic.

- **⚪ Diagnostic `Location` uses a zero-length span inconsistent with the line/column** — [XamlSourceGenerator.cs:211-215](Loom.UI.SourceGen/XamlSourceGenerator.cs#L211) · *correctness, conf: low* · IDE squiggles may land at file start.
- **⚪ Root element type resolved twice** — [XamlSourceGenerator.cs:75-78,106](Loom.UI.SourceGen/XamlSourceGenerator.cs#L75) · *maintainability, conf: medium* · trivial redundant lookup.

### 5 · `Loom.UI` layout & dependency-property parity — MU1 in flight

WinUI-divergence bugs. The Grid Star collapse is the most impactful; the rest are latent until styles/animation/binding land.

- **🟠 Grid Star tracks collapse to zero under an infinite measure → a default Grid in a StackPanel renders nothing** — [Grid.cs:68-88](Loom.UI/Controls/Grid.cs#L68) · [StackPanel.cs:18](Loom.UI/Controls/StackPanel.cs#L18) · *parity, conf: high*
  `leftover = hasFiniteSpace ? max(0, available-used) : 0` → under `+Infinity`, every Star track resolves to 0. An empty definition list defaults to one Star track. So a default Grid measured unconstrained (StackPanel stacking axis, Canvas) desires zero size and renders nothing. WinUI treats Star as Auto under an infinite constraint.
  *Fix:* size Star like Auto (measure unconstrained, take max) when the axis is infinite; proportional distribution only when finite.

- **🟡 Stretch alignment with an explicit Width/Height left/top-aligns instead of centering** — [FrameworkElement.cs:73-86](Loom.UI/FrameworkElement.cs#L73) · *parity, conf: medium*
  When alignment is Stretch but an explicit size prevents stretching, `pos` hits the `_ => slotStart` default (near edge). WinUI centers it.
  *Fix:* treat Stretch-with-explicit-size as Center for positioning.

- **🟡 Grid Auto sizing re-measures children twice with different constraints; O(tracks×children)** — [Grid.cs:35-103](Loom.UI/Controls/Grid.cs#L35) · [UIElement.cs:65](Loom.UI/UIElement.cs#L65) · *correctness, conf: medium*
  Auto track measures each child with `Size.Infinite`, then the final loop re-measures at the resolved cell size; for constraint-dependent content (future wrapping text) the final `DesiredSize` can differ from what sized the track → overflow/under-fill. Also quadratic.
  *Fix:* measure each child once against its resolved cell constraint (WinUI order: fixed → auto → star).

- **⚪ Coerce applied per-rung at write time, not to the resolved value** — [DependencyObject.cs:82-100](Loom.UI/DependencyObject.cs#L82) · *parity, conf: medium*
  Each rung stores its own coerced value; coercion never re-runs against the effective value or when a dependency changes. No DP defines a Coerce callback yet. Will misbehave once storyboards / min-max coercion land.
  *Fix:* coerce the resolved value after `TryResolve`, with a `CoerceValue` path that re-resolves on dependency changes.

- **⚪ `ContentControl` re-hosts on every Content set with no equality short-circuit** — [ContentControl.cs:15-34](Loom.UI/Controls/ContentControl.cs#L15) (Border has the guard at [:19](Loom.UI/Controls/Border.cs#L19)) · *correctness, conf: low*
  Re-assigning the same Content rebuilds the hosted child + invalidates layout, discarding state. Common in binding refresh.
  *Fix:* `if (field == value) return;` guard; reuse the hosted TextBlock when only the string changes.

### 6 · Enum design & half-baked enums

Several authoring enums are narrower than the features they'll need; some are consumed by non-exhaustive ternaries that *silently degrade* unknown members instead of failing loudly like `FormatMapping`/`StateMapping` do.

- **🟡 `SamplerAddress` consumed as a binary ternary that silently degrades unknown modes to Clamp** — [SamplerAddress.cs:11-18](Loom.Core/SamplerAddress.cs#L11) · [DirectX12Technique.cs:118-120](Loom.DirectX12/DirectX12Technique.cs#L118) · [RenderContext.cs:435](Loom.Vulkan/RenderContext.cs#L435) · *enum, conf: high*
  `{ Clamp, Repeat }` consumed as `==Repeat ? Wrap : Clamp`. Missing `MirrorRepeat`, `ClampToBorder` (the conventional shadow-map / post-process-kernel mode), `MirrorClampToEdge`. Any future member silently maps to Clamp on both backends.
  *Fix:* grow to `{Clamp, Repeat, MirrorRepeat, ClampToBorder, MirrorClampToEdge}` + exhaustive switch that throws; plumb a border colour for `ClampToBorder`.

- **🟡 `BlendMode` covers only None/AlphaOver/Additive; alpha blended with colour factors** — [BlendMode.cs:10-20](Loom.Core/BlendMode.cs#L10) · [Technique.cs:288-308](Loom.Vulkan/Technique.cs#L288) · [DirectX12Technique.cs:230-245](Loom.DirectX12/DirectX12Technique.cs#L230) · *enum, conf: high*
  No Premultiplied (the standard mode for correct UI/particle compositing — the in-flight UI track will likely need it), Multiply, screen, subtract. Both bakers reuse one `(src,dst)` tuple for *both* colour and alpha — wrong for premultiplied output and inexpressible.
  *Fix:* grow to ≥ `{None, AlphaOver, PremultipliedAlpha, Additive, Multiply}`; split colour-vs-alpha factors when the new modes land.

- **🟡 `ResourceKind`/`Format` anticipate no acceleration-structure or indirect-argument resources** — [ResourceKind.cs:8-33](Loom.Core/ResourceKind.cs#L8) · [AccessKind.cs:9-34](Loom.Core/AccessKind.cs#L9) · *future-proofing, conf: medium*
  `Buffer` is documented narrowly ("compute writes, draw reads as vertex data"); no indirect-args / AS / general-storage marker. `AccessKind` tops out at `ReadVertex`; `ResourceState` has no `IndirectArgument`/`AccelerationStructure`. (Overlaps the RT finding.)
  *Fix:* when GPU-driven/RT begins, add those states + access edges, and broaden/split `Buffer`.

- **⚪ `VertexFormat` is float-only — no packed/normalized/integer formats** — [VertexFormat.cs:11-24](Loom.Core/VertexFormat.cs#L11) · *future-proofing, conf: medium*
  `{Float, Float2, Float3, Float4}`; its own doc admits the gap. **M15 (in flight) explicitly needs** integer bone indices (`UByte4`/`UShort4`), weights, packed colours. The switch throws loudly, so honest friction — but a skinning demo can't express its layout today.
  *Fix:* add `UByte4`/`UShort4`/`UNorm8x4`/`Half2`/`Half4`/SNorm variants.

- **⚪ `VertexLayout` offset/stride ignores attribute alignment** — [VertexLayout.cs:47-58](Loom.Core/VertexLayout.cs#L47) · *future-proofing, conf: medium*
  Packs offsets purely additively; works only because every current format is 4-byte-granular. The moment a sub-4-byte format lands (packed colour, `Uint16x4` bone indices for M15), tight packing can place an attribute at a misaligned offset that disagrees with GPU fetch rules.
  *Fix:* round offsets/stride to required alignment when extending, or document tight-packing as intentional and all-4-byte.

- **⚪ `Format` and `TextureFormat` overlap with no explicit cross-reference** — [TextureFormat.cs:8-15](Loom.Core/TextureFormat.cs#L8) · [Format.cs:9-38](Loom.Core/Format.cs#L9) · *enum, conf: medium*
  Both carry `Rgba8Srgb`/`Rgba8Unorm`. The split is defensible (source-image encodings vs render-target formats — they diverge as BC/ASTC vs HDR/packed land), but neither doc states the overlap is intentional, inviting a future contributor to unify or duplicate-extend.
  *Fix:* keep them separate but cross-reference the boundary explicitly in the doc-comments.

### 7 · Maintainability, conventions & doc drift

None affect runtime correctness.

- **🟡 UI file names use lowercase `Ui` while the types inside are correctly `UI`-cased** — `Loom.Core/Ui{Vertex,DrawData,DrawCommand,PrimKind,Surface,SurfaceId}.cs`, [Loom.Vulkan/UiRenderer.cs](Loom.Vulkan/UiRenderer.cs), [Loom.DirectX12/DirectX12UiRenderer.cs](Loom.DirectX12/DirectX12UiRenderer.cs), [Loom.UI/IUiCompositor.cs](Loom.UI/IUiCompositor.cs), [Loom.UI/UiTrack.cs](Loom.UI/UiTrack.cs), [Loom.UI.Tests/UiDrawDataTests.cs](Loom.UI.Tests/UiDrawDataTests.cs) · *maintainability, conf: high*
  A grep for the identifier pattern `\bUi[A-Z]` returns **no** matches — every declared type is correctly `UI`-cased and CLAUDE.md's rule is honored *in code*. Only the file basenames carry lowercase `Ui` (likely from PlanningUI.md's spelling), diverging from the filename==type convention and inconsistent with the matching `UIElement.cs`/`UIRoot.cs`. Separately, **`UiTrack.cs` contains no type at all** — just an `[assembly: InternalsVisibleTo]` scaffold behind a misleading name.
  *Fix:* `git mv` the files to match their types (`UIVertex.cs`, …, `DirectX12UIRenderer.cs`, `IUICompositor.cs`); rename `UiTrack.cs` → `AssemblyInfo.cs`; reconcile PlanningUI.md's spelling.

- **🟡 Dead config knobs: `PreferDiscreteGpu` and `GpuBasedValidation` declared but never consumed** — [VulkanEngineSettings.cs:21-22](Loom.Vulkan/VulkanEngineSettings.cs#L21) · [DirectX12EngineSettings.cs:25](Loom.DirectX12/DirectX12EngineSettings.cs#L25) · *correctness, conf: high*
  `PickPhysicalDevice` unconditionally prefers the first discrete GPU; DirectX12 only calls `EnableDebugLayer()`, never `SetEnableGPUBasedValidation`. A host setting these gets silently-ignored behaviour. (Forcing the integrated GPU is useful for cross-backend adapter-matched diffing.)
  *Fix:* thread the flags through, or remove the properties until wired.

- **🟡 CLAUDE.md "Current state" has drifted** — [CLAUDE.md:16,54,31](CLAUDE.md#L16) · *docs, conf: high*
  The Projects list omits the entire `Loom.UI`/`Loom.UI.SourceGen`/`Loom.UI.Tests` track; line 54 claims "no golden-image tests yet" although [GoldenImageTests.cs](Loom.Tests/GoldenImageTests.cs) + [tools/golden.ps1](tools/golden.ps1) + committed `Goldens/` ship a full PNG-comparison harness; line 54 references `LOOM_CAPTURE` although line 31 says there are no `LOOM_*` runtime vars (it's `--capture`).
  *Fix:* add the UI projects; update the golden-tests claim; replace `LOOM_CAPTURE` with `--capture`.

- **🟡 Two independent reflection-JSON parsers with a divergent classification gate** — [ShaderReflection.cs](Loom.Vulkan/ShaderReflection.cs) · [DirectX12Shaders.cs:84-87,124-130](Loom.DirectX12/DirectX12Shaders.cs#L84) · *maintainability, conf: medium*
  Vulkan gates on both `descriptorTableSlot` *and* `resource`; DirectX12's `ResolveComputeStorage` omits the `descriptorTableSlot` gate. Benign today, but a future resource form could be counted by one backend and not the other → mismatched layouts (a parity break the dual-parser structure makes easy to introduce).
  *Fix:* extract one shared neutral reflection parser; at minimum align the gates.

- **🟡 DXIL toolchain (DXC) resolved from the machine's Windows SDK — non-hermetic** — [Loom.Shaders.targets:61-69](build/Loom.Sdk/Loom.Shaders.targets#L61) · *future-proofing, conf: high*
  `slangc` is bundled hermetically; DXC comes from whatever Windows SDK is installed, and a missing DXC only *Warns* (then DirectX12 silently runs stand-ins). Two machines can emit behaviour-different signed DXIL from identical source.
  *Fix:* pin/vendor DXC to a known version; promote "no DXC found" to an Error when `UseDirectX12=true`.

- **🟡 `GetCamera` receives a different time value than `ctx.Time` under frozen capture** — [Program.cs:203-214](Loom.Sandbox/Program.cs#L203) · [IDemo.cs:30-35](Loom.Sandbox/Demos/IDemo.cs#L30) · *correctness, conf: medium*
  Under capture, the camera-override time is `0.6f` while `ctx.Time` is `1.0f`, despite `GetCamera`'s doc saying its time is "exactly like the other per-frame inputs." No demo consumes it yet, but the moment a demo scrolls its camera from that arg (documented M14-6 intent), a frozen capture misaligns camera vs content — the same time/camera split CLAUDE.md says once cost a long false bug hunt.
  *Fix:* compute one `sceneTime` and pass it to `GetCamera`; keep a separate orbit angle only for the built-in perspective.

- **⚪ `mesh.slang` is compiled but referenced by no technique — dead build output** — [mesh.slang](Loom.Sandbox/Shaders/mesh.slang) · *maintainability, conf: high* · its own comment says "M12 retires this." Delete or add to `$(LoomShaderExclude)`.
- **⚪ Bootstrap `DirectX12Triangle` allocated for the engine's whole lifetime but only used by the dead legacy path** — [DirectX12Engine.cs:125,254](Loom.DirectX12/DirectX12Engine.cs#L125) · [DirectX12Triangle.cs](Loom.DirectX12/DirectX12Triangle.cs) · *maintainability, conf: high* · a permanent root-sig + PSO + runtime FXC dependency production never draws. Gate behind the legacy path or remove.
- **⚪ `--smoke-frames` accepts non-positive values that silently never terminate** — [LaunchOptions.cs:151,158](Loom.Sandbox/LaunchOptions.cs#L151) · *correctness, conf: high* · `--smoke-frames 0`/negative disables smoke mode → the documented interactive hang, no diagnostic. (`--max-fps` also accepts negatives.) Reject non-positive values in `Parse`.

### 8 · Tests — coverage gaps

- **🟡 `GoldenImageTests` passes vacuously in the default run** — [GoldenImageTests.cs:22-27](Loom.Tests/GoldenImageTests.cs#L22) · [ci.yml:33-34](.github/workflows/ci.yml#L33) · *tests, conf: high*
  Both early-returns (no `LOOM_GOLDEN_DIR`, or empty `Goldens/`) report **PASS** having asserted nothing; CI runs without the env var, so it's always a vacuous green. A broken capture step or regressed Goldens copy would still pass.
  *Fix:* use an explicit Skip so the run reports SKIPPED; fail loudly if `LOOM_GOLDEN_DIR` is set but `Goldens/` is empty.

- **🟡 `DependencyProperty` precedence ladder only partially asserted** — [DependencyPropertyTests.cs:47-66](Loom.UI.Tests/DependencyPropertyTests.cs#L47) · *tests, conf: medium*
  The "fire callback only on a resolved change" contract is verified only for the Local-from-Default case; Style/Inherited/Animation transitions never inspect the callback log. A regression where an inherited value fails to fire `PropertyChanged` (or clearing animation fires a spurious one) wouldn't be caught.
  *Fix:* assert the log after each precedence transition; add a masked-by-higher-precedence case that must NOT fire.

- **🟡 Box-filter odd-dimension / edge-tap clamp asserted only for dimensions, never pixel values** — [TextureDataTests.cs:58-76](Loom.Tests/TextureDataTests.cs#L58) · [TextureData.cs:81,85](Loom.Core/TextureData.cs#L81) · *tests, conf: high*
  The only value test is an even 2×2 (no clamp); odd reductions use solid colours (any tap choice yields the same byte). An off-by-one in odd-dimension tap selection would pass every current test yet break the cross-backend byte-identical-mip guarantee.
  *Fix:* add a value test on a non-uniform odd-dimension image (3×1 / 3×3 gradient) asserting exact mip bytes.

- **⚪ Backends have zero unit tests** — [Loom.Tests.csproj:29-34](Loom.Tests/Loom.Tests.csproj#L29) · *tests, conf: medium*
  No backend reference; the pipeline/descriptor hash caches, placed-resource aliasing translation, and marshaling seam are covered only by GPU goldens that don't run in CI. A defensible quarantine boundary, but the CPU-testable slices (hash-key equality, neutral-decision→barrier/PSO tables, alias-offset math) could be extracted and tested.
  *Fix:* extract device-free portions behind plain types + a small backend-logic test project.

- **⚪ No test covers a compute→compute storage hazard or a ping-pong handle-swap topology** — [BarrierPlanTests.cs:134-150](Loom.Tests/BarrierPlanTests.cs#L134) · *tests, conf: medium*
  Only one compute→raster edge is tested. The ping-pong-via-handle-swap invariant (§8) and the Storage→Storage RAW/WAW path (GPU-particle sim-step shape) are exercised by demos but not pinned by a derivation test.
  *Fix:* add a compute→compute `ReadWrites` test (assert WAW dependency) and a ping-pong test that swaps two handles and asserts dependencies follow the handles.

---

## Positive confirmations (what's well-designed)

Recorded so the audit shows these dimensions were checked and *passed*:

- **Backend isolation verified clean** across Core, Graph, Assets, and UI — grep for `Silk.NET`/`Vk*`/`D3D12`/`DllImport`/`Marshal.`/`stackalloc` in Core/Graph returns only doc-comments + benign `[StructLayout]` usings. The cardinal invariant holds in the actual code.
- **Neutral matrix/NDC convention** (RH, +Y up, `System.Numerics`) with per-backend clip fix-up (Vulkan Y-flip, DirectX12 negative-height viewport) — keeps Metal/WebGPU/console's `[0,1]`-depth NDC open without touching authoring code. The hardest cross-API portability trap is already avoided.
- **`BarrierPlan`/`AliasPlan` are advisory backend inputs, not mandates** — a WebGPU backend can drop both (no explicit barriers / no aliasing control) while still using the `AttachmentAction` load/store ops WebGPU *does* require. This is the key reason WebGPU stays open.
- **Derived per-attachment Load/Store actions** make tile-based (Adreno/Mali/Metal memoryless) on-chip load/store fully representable — the `.Writes`-vs-`.Blends` distinction carries exactly the load intent a tiler needs, for free.
- **Opaque `object surface` + `EngineSettings` subclassing** is a clean backend-creation seam — a Metal `CAMetalLayer` / WebGPU canvas / console swapchain is just another `object` + settings subclass; no windowing/graphics-API type leaks into Core.
- **The Execute layer preserves a full dependency DAG** (`ScheduledPass.DependsOn`) with per-pass Queue intent rather than collapsing to a flat list — multi-queue/reordering is structurally accommodated *at the seam*; the gap is only the missing derivation + backend queues.

*(One mild caveat in this area: the native ImGui `nint ImDrawData*`/`ImTextureID` crosses the otherwise pointer-free `IEngine`/`FrameInputs` boundary — debug-only, gated on `EnableImGui`, mild wasm friction; the pointer-free `UISurfaces → UIDrawData` path is the alternative the engine is migrating toward.)*

---

## Appendix — how this was produced

17 reviewers (Loom.Core, Loom.Graph, Vulkan core + pipeline, DirectX12 core + pipeline, Assets, Sandbox, UI, UI.SourceGen, tests, shaders+build; plus cross-cutting parity, invariants, forward-backends, forward-features, enums) each read the actual files and produced grounded findings. Every finding was handed to an independent adversarial verifier that re-opened the cited code and tried to refute it (defaulting to rejection for unconfirmable correctness claims). A synthesis pass deduped across reviewers, applied verifier severity adjustments, and ranked. 5 findings were rejected as false positives / misreadings and are not listed here.
