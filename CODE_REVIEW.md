# Loom — Code Review Findings

**Date:** 2026-06-20
**Method:** Exhaustive multi-agent audit — 17 reviewers (per-project + cross-cutting dimensions) fanned out, every finding was re-checked by an independent adversarial verifier that re-opened the cited code, then a synthesis pass deduped and ranked. 101 raw findings checked, **96 confirmed, 5 rejected** as false positives.
**Scope:** all 10 projects. M15 (game-archetype track) and MU1 (UI) are being written by other agents in parallel — findings in that territory are flagged as likely work-in-progress.
**Axes:** architectural drift · correctness · cross-backend parity · maintainability · forward-looking dead-ends (backends + advanced GPU features) · half-baked enums.

---

## Final-state reconciliation (updated 2026-06-24 — post cleanup sprint)

> Previous reconciliation covered commit `6631217` through M15 + MU1. This pass audited the current tree (commit `ada8f1d` and its predecessors on `Review`), verifying every finding by re-opening the cited file.
>
> **39 findings closed; ~19 remain.** The cleanup sprint resolved every 🟠 Major correctness bug except the SUIsei.SourceGen incremental-pipeline architectural issue (all symptom-level defects under it are closed), and a substantial fraction of the 🟡 Minor and ⚪ Note findings.
>
> **Newly resolved since last reconciliation:**
> - **Correctness bugs:** descriptor-ring throw-on-overflow ✅; gamma-correct sRGB mips ✅ (LUT + new test); glTF walks scene graph with WorldMatrix ✅; bake-cache hash includes `ByteSize` ✅; `UsesOf` same-pass Sample+Write throws `GraphValidationException` ✅; `VulkanContext` ctor wraps bring-up in try/catch+Dispose ✅; `AssetWatcher` `_disposed` flag under gate ✅; `LayeredAssetSource` path-traversal containment check ✅; `RecordRasterPass` `colorCount >= 8` guard ✅.
> - **Async-compute façade:** `Queue.AsyncCompute` now documented as "not yet honoured"; `GraphSchedule` throws loudly at derive time ✅.
> - **DXIL build:** `|| exit /b 0` removed; per-shader stage probe via `HasVertex`/`HasFragment`/`HasCompute`; declared-but-failing stages hard-fail the build ✅.
> - **SUIsei.SourceGen (4 of 5 defects closed):** hint name uses full class (no collision) ✅; `x:Name` validated with `SyntaxFacts.IsValidIdentifier` + keyword check + uniqueness ✅; literal/enum validated before emit ✅; namespace gated on `Ui`/`None` ✅; cancellation checked in `EmitElement` ✅; `IgnoredContent` diagnostic for text nodes ✅. *Incrementality remains open — `CompilationProvider` is still combined without narrowing.*
> - **SUIsei layout:** Grid Star sizes like Auto under infinite constraint ✅; Stretch-with-explicit-size centers instead of near-aligning ✅.
> - **SUIsei DP tests:** full rung-to-rung callback test + masked-by-higher-precedence test ✅.
> - **Config knobs:** `PreferDiscreteGpu` consumed in both backends; `GpuBasedValidation` wired in DirectX12 ✅.
> - **Parity:** Vulkan `Dispatch` now honors `LinearFilter` (not hardcoded nearest) ✅; DirectX12 PSO cache key folds `TemplateDigest` ✅; instance vertex stride uses `InstanceStride` from baked technique ✅.
> - **Tests:** `GoldenImageTests` throws `SkipTestException` (no more vacuous pass) ✅; odd-dimension edge-clamp tap test ✅; sRGB-vs-linear mip test ✅.
> - **Maintainability:** `ShaderReflection` consolidated into `Loom.Graph` (one parser, aligned gate) ✅; UI file names fixed (no `Ui*.cs` outside `external/`) ✅; `UiTrack.cs` removed ✅; `ContentControl` re-host equality guard ✅; `--smoke-frames` rejects non-positive values ✅; `CLAUDE.md` updated ✅; `VertexFormat` gained `UInt4`, `Half4`, `UNorm4` ✅; `BlendMode` gained `PremultipliedAlpha` ✅; **DXIL toolchain hermetic** — `Microsoft.Direct3D.DXC` NuGet package added to all shader-compiling projects; `Loom.Shaders.targets` prefers `$(PkgMicrosoft_Direct3D_DXC)` over Windows SDK ✅.
>
> **Still standing (see per-finding detail below):** SUIsei.SourceGen incremental pipeline; `SamplerAddress` enum still only `{Clamp, Repeat}` (exhaustive switch added — silent degradation fixed — but `MirrorRepeat`/`ClampToBorder` not added); `BlendMode` still missing `Multiply` and colour-vs-alpha factor split; `GetCamera`/`ctx.Time` diverge in capture mode; cross-backend parity still has no automated asserting test; `mesh.slang` still a dead build output; DP coerce-per-rung not yet fixed; `VertexLayout` still purely additive offsets. Forward-looking gaps (RT, bindless, GPU-driven, temporal) are unchanged and expected.

---

## Executive summary

Loom is **architecturally healthy at its core.** The cardinal invariant — no graphics-API type in `Loom.Core`/`Loom.Graph` — was independently verified clean, the Describe/Execute/Backend separation holds, and several forward-looking seams are genuinely well-designed (see *Positive confirmations* at the end): the neutral RH/+Y-up matrix convention with per-backend clip fix-up, the *advisory* (non-mandatory) `BarrierPlan`/`AliasPlan`, derived per-attachment Load/Store actions, the opaque `object surface` creation seam, and the preserved dependency DAG in the Execute layer.

**Risk concentrates in three places:**

1. **A cluster of latent correctness bugs** masked by current demo usage but which will fire as the engine scales: the DirectX12 descriptor-ring mid-frame wrap can silently corrupt per-pass descriptors, sRGB mips are box-filtered in gamma space (visibly wrong albedo on *both* backends), the bake-cache hash omits buffer `ByteSize`, and glTF loading ignores node transforms entirely.
2. **The async-compute / multi-queue façade** — `Queue.AsyncCompute` and `.Queue()` are authorable and documented as honored, but no layer derives cross-queue sync and neither backend creates a compute queue. The API actively lies about a named forward-looking pillar.
3. **The brand-new `SUIsei.SourceGen` XAML generator** (MU0/MU1, in flight) has several real correctness defects — broken incrementality, hint-name collisions, and unvalidated input that surfaces as confusing generated-code compile errors.

**Cross-backend parity — the headline invariant — has no automated asserting test**, despite the comparison infrastructure already existing.

The **forward-looking gaps** (ray tracing, bindless, GPU-driven indirect, temporal history) are mostly *honest, additive friction rather than irreversible dead-ends* — helped by backend isolation and switch statements that throw loudly on unhandled members. The exceptions worth fixing *now* are the over-promising APIs: the async-compute façade, and the persistent history-ring `Count` field that is hashed-but-never-allocated.

### Counts by severity

*Original counts at review time; counts in parentheses are the remaining open findings as of 2026-06-24.*

| Severity | Original | Remaining | Meaning |
|---|---|---|---|
| 🔴 Critical | 0 | 0 | breaks correctness/portability now, or irreversible future dead-end |
| 🟠 Major | 11 | 1 | real bug / serious drift / significant friction |
| 🟡 Minor | ~33 | ~12 | quality / maintainability / latent traps |
| ⚪ Note | ~25 | ~8 | nits, observations, + 7 *positive* confirmations |

---

## Top priorities (fix first)

1. ✅ **~~🟠 DirectX12 descriptor-ring mid-frame wrap~~** — **RESOLVED.** `Allocate()` now throws `InvalidOperationException` on overflow instead of wrapping back into the live region.
2. ✅ **~~🟠 Gamma-incorrect sRGB mip generation~~** — **RESOLVED.** `Downsample` branches on `format`; `Rgba8Srgb` channels decode through a 256-entry `SrgbToLinear` LUT, average in linear-light 16-bit fixed-point, and re-encode via `LinearToSrgb`. Covered by a new test (`Srgb_mips_average_in_linear_light_not_gamma_space`).
3. ✅ **~~🟠 Resolve the async-compute façade~~** — **RESOLVED** (option b). `Queue.cs` now documents `AsyncCompute` as "not yet honoured"; `GraphSchedule.Derive` throws a clear error when a pass declares it.
4. ✅ **~~🟠 Bake-cache hash omits buffer `ByteSize`~~** — **RESOLVED.** Hash line now appends `b={resource.ByteSize}`, matching `ResourceDto`.
5. **🟠 `SUIsei.SourceGen` incremental-pipeline** — **Partially resolved.** All four symptom-level defects are closed: hint names use the full class (`MakeHintName(fullClass, …)`), `x:Name` is validated with `SyntaxFacts.IsValidIdentifier`, literal/enum values are pre-validated, namespace is gated on `Ui`/`None`. The root architectural issue (`CompilationProvider` combined without narrowing → full re-run on every C# edit) is not yet fixed. *See §4 for full status.*
6. ✅ **~~🟠 glTF node-transform omission~~** — **RESOLVED.** Loader now walks `LogicalNodes`, bakes `WorldMatrix` into static-mesh vertices (with inverse-transpose for normals), and emits skinned meshes once per skin.
7. ✅ **~~🟠 DXIL build passes green on DX-only shader failures~~** — **RESOLVED.** `|| exit /b 0` removed. Per-shader `HasVertex`/`HasFragment`/`HasCompute` detection skips absent entry points cleanly; a declared stage that fails to compile to DXIL hard-fails the build.
8. **🟡 Add an automated cross-backend parity test** — still open. The `ImageComparison` comparator and `GoldenImageTests` harness exist; no test diffs a Vulkan capture against a DirectX12 one.

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

- ✅ **~~🟠 DirectX12 descriptor-ring mid-frame wrap~~** — **RESOLVED.** `Allocate()` now throws `InvalidOperationException` on overflow with a clear budget message. The double-buffered region (per frame-in-flight slot) also prevents inter-frame clobber.

- ✅ **~~🟠 sRGB mip generation box-averages in gamma space, not linear~~** — **RESOLVED.** `Downsample` now branches on `format`; for `Rgba8Srgb` it decodes through a precomputed `SrgbToLinear[256]` table (→ 16-bit linear), averages four samples, and re-encodes via `LinearToSrgb[65536]`. Alpha and UNORM channels stay integer. A new test (`Srgb_mips_average_in_linear_light_not_gamma_space`) pins the output.

- ✅ **~~🟠 glTF loader ignores node transforms and scene graph~~** — **RESOLVED.** Loader now walks `model.LogicalNodes` — bakes each node's `WorldMatrix` into static-mesh vertices (inverse-transpose for normals/tangents), emits skinned meshes once per skin. Multiply-referenced meshes are placed at each referencing node.

- ✅ **~~🟠 `RenderGraph.Hash()` omits buffer `ByteSize`~~** — **RESOLVED.** Hash line appends `b={resource.ByteSize}`, matching the `ResourceDto` serialisation.

- ✅ **~~🟡 `NativeArena.Alloc` size is a 32-bit multiply that can overflow~~** — **RESOLVED** (file moved to VMASharp external module). Verified the underlying pattern no longer appears in the in-tree Vulkan backend.

- ✅ **~~🟡 `UsesOf` collapses same-pass Sample+Write of one handle to the write state~~** — **RESOLVED.** The flagged priority-reduction now first checks `f.Sample && (f.Write || f.Blend)` and throws `GraphValidationException` with a clear message: *"use two passes, or a ping-pong handle pair."*

- ✅ **~~🟡 `AssetWatcher` can fire `Changed` after `Dispose`~~** — **RESOLVED.** `_disposed` bool set under `_gate` first in `Dispose`; checked at the top of `Flush` so an in-flight debounce callback bails before invoking `Changed`.

- ✅ **~~🟡 `VulkanContext` ctor leaks instance/surface/device on mid-bring-up failure~~** — **RESOLVED.** Bring-up is now wrapped in `try { … } catch { Dispose(); throw; }`. `Dispose` is handle-guarded, so partial teardown is safe.

- ✅ **~~🟡 `LayeredAssetSource.TryResolve` does no path-traversal containment~~** — **RESOLVED.** Rejects rooted `logicalName`, then after `Path.GetFullPath(Path.Combine(root, logicalName))` asserts the result starts with `root + Path.DirectorySeparatorChar`.

- ✅ **~~⚪ `RecordRasterPass` writes fixed-size `stackalloc[8]` with no bound on `colorCount`~~** — **RESOLVED.** `DirectX12Engine.FrameRecord.cs` now has `if (colorCount >= 8) throw …` before the `stackalloc`.

- **⚪ `ImageLoader` surfaces low-context exceptions with no path / null-decode guard** — [ImageLoader.cs:19-30](Loom.Assets/ImageLoader.cs#L19) · *correctness, conf: medium* · Still open.

### 2 · Async-compute / multi-queue façade

✅ **RESOLVED.** `Queue.cs` now documents `AsyncCompute` explicitly as *"not yet honoured — assigning it currently fails at derive time."* `GraphSchedule.Derive` throws a descriptive error when any pass requests `Queue.AsyncCompute`, so the API fails loud instead of silently running on Graphics. The underlying implementation gap (cross-queue partitioning, semaphore edges, compute queue acquisition) remains deferred, but the API no longer lies about it.

### 3 · Cross-backend parity risks

Points where the two backends derive or apply something on different principles — latent traps masked by current usage.

- ✅ **~~🟠 DXIL compile errors silently swallowed by `|| exit /b 0`~~** — **RESOLVED.** `|| exit /b 0` removed. `Loom.Shaders.targets` now probes each shader's source for `vertexMain`/`fragmentMain`/`computeMain` via MSBuild item metadata (`HasVertex`/`HasFragment`/`HasCompute`) and runs exactly those stages. A missing entry point is simply skipped (no error); a declared entry point that fails DXIL compilation hard-fails the build with a non-zero exit.

- **🟡 Vulkan-vs-DirectX12 parity is never exercised by any test** — [ImageComparison.cs:3-4](Loom.Tests/ImageComparison.cs#L3) · *parity, conf: high* · Still open. `GoldenImageTests` now skips (rather than vacuously passes) when `LOOM_GOLDEN_DIR` is unset, and the comparison infrastructure is there — but no test diffs Vulkan vs DirectX12 captures head-to-head. Fix: an opt-in GPU-gated test asserting `FailedFraction < 0.005` at tolerance 4.

- ✅ **~~🟡 DirectX12 PSO cache key omits template/bytecode content~~** — **RESOLVED.** `BuildKey` now appends `|t{TemplateDigest(template)}`, folding bytecode + blend + layout + instance into the key, mirroring Vulkan.

- ✅ **~~🟡 Compute sampled-input filtering diverges: Vulkan hardcodes nearest~~** — **RESOLVED.** Vulkan's `Dispatch` path now selects `_linearSampler` or `_nearestSampler` from `template.LinearFilter`, exactly as `BindSampledInputs` does for raster.

- ✅ **~~🟡 Instance-from-buffer vertex stride differs between backends~~** — **RESOLVED.** DirectX12 now uses `baked.InstanceStride` (from `template.Instance.Stride`) when non-zero, falling back to `byteSize/count` only when the technique declares no instance layout — matching Vulkan's source of truth.

- **⚪ Game-UI overlay seam exercised by only one demo** — [CubeLightingDemo.cs:33](Loom.Sandbox/Demos/CubeLightingDemo.cs#L33) · *parity, conf: medium* · Still open (low risk; multiple UI demos now exist).


### 4 · `SUIsei.SourceGen` (XAML generator)

MU1 is shipped; the generator is in active use. Four of five original defects are closed.

- **🟠 Incremental pipeline combines the raw `Compilation`, defeating caching** — [XamlSourceGenerator.cs:83-85](external/SUIsei/SUIsei.SourceGen/XamlSourceGenerator.cs#L83) · *architecture, conf: high* · **Still open.**
  The XAML input is now projected to a value-equatable `XamlInput(Path, Text)` record before the combine, which is an improvement. However `.Combine(context.CompilationProvider)` is still called without narrowing the `Compilation` — any C# file edit changes `Compilation`, which has no value equality, so the `(XamlInput, Compilation)` tuple is considered changed and `Generate` re-runs in full. The canonical "incremental in name only" anti-pattern persists at the pipeline level even though the downstream output problems are all closed.
  *Fix:* narrow `CompilationProvider` to only the type symbols the generator actually needs (e.g. `.Select(c => c.GetTypeByMetadataName("SUIsei.UIElement"))`) before combining.

- ✅ **~~🟠 `AddSource` hint name uses the short class name → namespace collision~~** — **RESOLVED.** `MakeHintName(fullClass, suffix)` sanitises the fully-qualified class name (replaces non-alphanumeric with `_`), so `A.MainView` → `A_MainView.g.cs` and `B.MainView` → `B_MainView.g.cs`.

- ✅ **~~🟠 `x:Name` / generated variable names emitted verbatim with no validation~~** — **RESOLVED.** `TryValidateName` uses `SyntaxFacts.IsValidIdentifier` + keyword-kind check; `scope.Names` enforces uniqueness per scope; `Root` is seeded in `gen.UsedNames` at construction so any user `x:Name='Root'` collides and diagnoses.

- ✅ **~~🟠 Literal/enum conversion emits unvalidated text~~** — **RESOLVED.** `TryLiteral` checks enum members via `type.GetMembers(value).OfType<IFieldSymbol>().Any()`, validates bool case-insensitively, and `TryParse`s numeric types — returning `LiteralStatus.BadValue` (→ `SUISEI012` diagnostic) instead of emitting uncompilable text.

- ✅ **~~🟡 Element XML namespace ignored~~** — **RESOLVED.** `EmitElement` now gates resolution on `el.Name.Namespace == Ui || el.Name.Namespace == XNamespace.None`; elements in any other namespace are reported as `UnknownElement` (SUISEI003) rather than silently resolving.

- ✅ **~~🟡 No cancellation checks inside the recursive emit walk~~** — **RESOLVED.** `gen.Ct.ThrowIfCancellationRequested()` is called at the top of `EmitElement` and at each child-element iteration.

- ✅ **~~🟡 Text content / property-element syntax silently dropped~~** — **RESOLVED.** Text nodes with non-whitespace content now call `gen.Report(IgnoredContent, textNode, "text content")` (SUISEI013 warning). Property elements emit `BadPropertyElement` (SUISEI020) for unrecognised syntax, and `IgnoredContent` for namespaced/dot-separated attributes the handler doesn't process.

- **⚪ Diagnostic `Location` uses a zero-length span** — [XamlSourceGenerator.cs:1569-1576](external/SUIsei/SUIsei.SourceGen/XamlSourceGenerator.cs#L1569) · *correctness, conf: low* · Still open — `TextSpan(start, 0)` → IDE squiggles may land at the correct line but with no range highlight.
- ✅ **~~⚪ Root element type resolved twice~~** — **RESOLVED.** `gen.RootType = type` is assigned once in `EmitElement` (when `isRoot: true`) and not re-looked-up.

### 5 · `SUIsei` layout & dependency-property parity

MU1 is shipped. Three of five findings closed.

- ✅ **~~🟠 Grid Star tracks collapse to zero under an infinite measure~~** — **RESOLVED.** `ResolveTracks` now branches on `finite = !float.IsPositiveInfinity(available)`; when the axis is infinite, Star tracks are sized like Auto via `AutoExtent(i, horizontal)` instead of receiving a share of zero leftover.

- ✅ **~~🟡 Stretch alignment with an explicit Width/Height left/top-aligns instead of centering~~** — **RESOLVED.** `ArrangeAxis` now uses `1 or 3 => slotStart + (slotSize - size) / 2f` — both Center (1) and Stretch (3) center when the element can't fill its slot.

- **🟡 Grid Auto sizing re-measures children twice with different constraints; O(tracks×children)** — [Grid.cs:35-103](external/SUIsei/SUIsei/Controls/Grid.cs#L35) · *correctness, conf: medium* · Still open. Children are measured once against cell constraints before `ResolveTracks`, but the measure / track-resolve ordering is still Auto-then-Star, which can disagree with WinUI's fixed→auto→star single-pass. Low risk until wrapping text or aspect-ratio content arrives.

- **⚪ Coerce applied per-rung at write time, not to the resolved value** — [DependencyObject.cs:82-100](external/SUIsei/SUIsei/DependencyObject.cs#L82) · *parity, conf: medium* · Still open. No DP defines a Coerce callback yet; will matter once storyboards / min-max coercion land.

- ✅ **~~⚪ `ContentControl` re-hosts on every Content set with no equality short-circuit~~** — **RESOLVED.** Content setter now has `if (Equals(field, value)) return;` before any re-host logic.

### 6 · Enum design & half-baked enums

Several authoring enums are narrower than the features they'll need; some are consumed by non-exhaustive ternaries that *silently degrade* unknown members instead of failing loudly like `FormatMapping`/`StateMapping` do.

- **🟡 `SamplerAddress` silent degradation on unknown modes** — [SamplerAddress.cs](Loom.Core/Materials/SamplerAddress.cs) · [DirectX12Technique.cs](Loom.DirectX12/DirectX12Technique.cs#L440) · *enum, conf: high* · **Partially resolved.** The binary ternary is gone — both backends now use an exhaustive switch that `throw`s on an unknown member. The enum itself is still only `{Clamp, Repeat}`; `MirrorRepeat`, `ClampToBorder`, `MirrorClampToEdge` are not yet added. Silent degradation is fixed; the narrow authoring surface remains.

- **🟡 `BlendMode` missing modes and shared colour/alpha factors** — [BlendMode.cs](Loom.Core/Graph/BlendMode.cs) · *enum, conf: high* · **Partially resolved.** `PremultipliedAlpha` has been added (the UI compositor needs it). `Multiply`, screen, subtract are still absent; colour-vs-alpha factor split still not done.

- **🟡 `ResourceKind`/`Format` anticipate no acceleration-structure or indirect-argument resources** — [ResourceKind.cs](Loom.Core/Graph/ResourceKind.cs) · [AccessKind.cs](Loom.Core/Graph/AccessKind.cs) · *future-proofing, conf: medium* · Still open. Unchanged; expected until GPU-driven/RT work begins.

- ✅ **~~⚪ `VertexFormat` is float-only~~** — **RESOLVED.** `VertexFormat` now includes `UInt4` (bone indices), `Half4` (half-precision 4-channel), and `UNorm4` (packed RGBA8 unsigned-normalised). Both backends map all three.

- **⚪ `VertexLayout` offset/stride ignores attribute alignment** — [VertexLayout.cs](Loom.Core/Geometry/VertexLayout.cs) · *future-proofing, conf: medium* · Still open. Still purely additive; safe while all formats remain 4-byte-granular.

- **⚪ `Format` and `TextureFormat` overlap with no explicit cross-reference** — [TextureFormat.cs](Loom.Core/Materials/TextureFormat.cs) · [Format.cs](Loom.Core/Graph/Format.cs) · *enum, conf: medium* · Still open. No cross-reference added to the doc-comments.

### 7 · Maintainability, conventions & doc drift

None affect runtime correctness.

- ✅ **~~🟡 UI file names use lowercase `Ui`~~** — **RESOLVED.** No `Ui*.cs` files remain outside `external/`. The Loom.Core UI draw-list types were reorganised into correctly-named files in `Loom.Core/Hosting/` (`DrawCall.cs`, `DrawListData.cs`, etc.). Backend renderers were also renamed. `UiTrack.cs` was removed.

- ✅ **~~🟡 Dead config knobs: `PreferDiscreteGpu` and `GpuBasedValidation` declared but never consumed~~** — **RESOLVED.** `VulkanEngine` passes `settings.PreferDiscreteGpu` to `VulkanContext.PickPhysicalDevice`; `DirectX12Context` reads `settings.GpuBasedValidation` to call `SetEnableGPUBasedValidation(true)` on the debug1 interface.

- ✅ **~~🟡 CLAUDE.md "Current state" has drifted~~** — **RESOLVED.** `SUIsei`/`SUIsei.SourceGen`/`SUIsei.Tests` added to Projects list; golden-image harness documented accurately; `--capture` correctly identified as the sandbox flag (not `LOOM_CAPTURE`).

- ✅ **~~🟡 Two independent reflection-JSON parsers with a divergent classification gate~~** — **RESOLVED.** `ShaderReflection.cs` is now in `Loom.Graph` (a single shared neutral parser). Both backends consume it.

- ✅ **~~🟡 DXIL toolchain (DXC) resolved from the machine's Windows SDK — non-hermetic~~** — **RESOLVED.** `Microsoft.Direct3D.DXC` NuGet package (v1.9.2602.24) added to all four shader-compiling projects (`Loom.DirectX12`, `Loom.Sandbox`, `Loom.UI.ImGui`, `Loom.UI.SUIsei`) with `GeneratePathProperty="true"`. `Loom.Shaders.targets` now prefers `$(PkgMicrosoft_Direct3D_DXC)\build\native\bin\$(LoomDxcArch)` as the DXC directory, with the Windows SDK glob as a fallback for machines that lack the reference. Both DXC and slangc are now hermetic NuGet-managed build tools.

- **🟡 `GetCamera` receives a different time value than `ctx.Time` under frozen capture** — [Program.cs](Loom.Sandbox/Program.cs) · *correctness, conf: medium* · Still open. `camTime = 0.6f` but `ctx.Time = 1.0f` in non-animated capture mode. No demo reads `camTime` from `GetCamera` yet, but the split will cause a camera/content misalignment the moment one does.

- **⚪ `mesh.slang` is compiled but referenced by no technique — dead build output** — [mesh.slang](Loom.Sandbox/Shaders/mesh.slang) · *maintainability, conf: high* · Still open. The shader still exists and is compiled. Its inline comment still contains a milestone reference ("M12 retires this").
- **⚪ Bootstrap `DirectX12Triangle` allocated for the engine's whole lifetime** — [DirectX12Engine.cs](Loom.DirectX12/DirectX12Engine.cs#L119) · [DirectX12Triangle.cs](Loom.DirectX12/DirectX12Triangle.cs) · *maintainability, conf: medium* · Still open. `_triangle` is created at construction and called from `RenderLegacy` (the `Graph is null` fallback path). The path is legitimate — it's the pre-graph bootstrap state — but it permanently allocates a root-sig + PSO + FXC dependency that production paths don't use.
- ✅ **~~⚪ `--smoke-frames` accepts non-positive values~~** — **RESOLVED.** `LaunchOptions` now validates `smokeFrames <= 0` and returns a descriptive failure message. `--max-fps` also validates `< 0`.

### 8 · Tests — coverage gaps

- ✅ **~~🟡 `GoldenImageTests` passes vacuously in the default run~~** — **RESOLVED.** Both early-returns now `throw new TUnit.Core.Exceptions.SkipTestException(…)` — the run reports SKIPPED instead of PASS when `LOOM_GOLDEN_DIR` is unset. When the env var is set but `Goldens/` has no baselines, the test throws `InvalidOperationException` (loud fail, not vacuous pass).

- ✅ **~~🟡 `DependencyProperty` precedence ladder only partially asserted~~** — **RESOLVED.** `Every_precedence_transition_that_changes_the_resolved_value_fires_the_callback` walks Inherited→Style→Local→Animation→clear, asserting the callback log at each step. `A_lower_precedence_change_masked_by_a_higher_rung_does_not_fire` adds the must-NOT-fire case.

- ✅ **~~🟡 Box-filter odd-dimension / edge-tap clamp asserted only for dimensions, never pixel values~~** — **RESOLVED.** `Box_filter_clamps_edge_taps_on_a_1xN_column` tests a 1×2 non-uniform column `{40, 80}` and asserts the 1×1 result is exactly `60` — a value assertion on a non-trivially-uniform input that catches off-by-one tap selection.

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
