# Loom — Comment & Documentation Review

**Original audit:** 2026-06-20  
**Last updated:** 2026-06-24 (after "Doc-purity sweep" — commit ada8f1d)  
**Method:** Mechanical sweep (grep) for milestone/section/narration tells across all `.cs`, then 6 parallel reviewers (one per project cluster) judging what grep can't: public-API doc coverage, non-self-contained prose, stale comments, and concrete rewrites.  
**Standard applied (the owner's):** A comment should describe **what the code does / why**, and be **self-contained** — a reader should never have to go look up a milestone or a planning-doc section to understand it. Public APIs should be *well documented*. Notes guiding future development, and explanations of a complex algorithm or gotcha, are welcome — as long as they stand on their own.

---

## Progress since the original audit

The sweep commit (ada8f1d) cleared the high-severity items cleanly:

- **Category A (stale narration):** 7 of 8 resolved — class summaries, interface docs, and backend descriptions all updated.
- **Category F (exception messages + dead const):** Fully cleared — the three "lands in a later slice" messages are gone, `NextSlice` const deleted.
- **Category G (gotchas with tags):** Mostly cleared — the scRGB HDR rationale, the Y-down clip-space explanation, the GltfLoader triangle filter, and the particle-stride comment all had their tags stripped while keeping the explanation. ImGui renderer code was refactored; M13 refs gone.
- **CLAUDE.md convention:** Added and in effect.
- **Three doc gaps closed:** `VertexLayout` constructors, `VulkanContext` property block, `LayeredAssetSource` now documented.

What remains is the B/C/D mechanical strip (~247 combined hits), three bug-archaeology instances, one stale A, three doc gaps, and the `external/SUIsei` cleanup.

---

## What still needs fixing

### A. Stale narration *(1 remaining)*

- [DirectX12RenderContext.cs:23](Loom.DirectX12/DirectX12RenderContext.cs#L23) — *"Compute and ImGui land in following slices."* Compute is implemented. Drop the sentence or replace with a factual summary of what the class handles.

### B. Milestone & phase references *(~95 hits)*

The bulk is spread across the Vulkan engine and mesh/material layer:

- [VulkanEngine.cs](Loom.Vulkan/VulkanEngine.cs) — `§12 M13 profiler`, `M7-B4`, `M12`, `§7.4`, `§7.6`, `M13` (hot-reload watcher, readback staging, GPU timing, shader loading)
- [VulkanContext.cs:43,52,152,309](Loom.Vulkan/VulkanContext.cs#L43) — `M13 GPU profiler`, `M7-A7` (HDR)
- [VulkanMeshStore.cs:15,29,51,74,98,101](Loom.Vulkan/VulkanMeshStore.cs#L15) — `Planning.md §12 M5`, `M12`, `M15`, `M15-0`
- [VulkanInstanceUploadBuffer.cs:9,37](Loom.Vulkan/VulkanInstanceUploadBuffer.cs#L9) — `M12`, `M15-0`
- [Loom.Tests/PngReader.cs:8](Loom.Tests/PngReader.cs#L8) — `M13 golden-image harness` in class summary
- [external/SUIsei/SUIsei/DependencyObject.cs:70](external/SUIsei/SUIsei/DependencyObject.cs#L70) — `MU4 correctness`
- [VulkanEngine.FrameRecord.cs:19,148](Loom.Vulkan/VulkanEngine.FrameRecord.cs#L19) — `M13` inline leads

### C. Planning-doc / `§`-section references *(~152 hits, down from ~245)*

~93 removed by the sweep; ~152 remain. Concentrated in:

- [external/SUIsei/SUIsei/DependencyObject.cs:4](external/SUIsei/SUIsei/DependencyObject.cs#L4) and [DependencyProperty.cs:12,47](external/SUIsei/SUIsei/DependencyProperty.cs#L12) — `PlanningUI.md §D1.2` (three occurrences; the surrounding prose already states the design — the citation is redundant)
- [VulkanFormatMapping.cs:7](Loom.Vulkan/VulkanFormatMapping.cs#L7) — `Planning.md §5`
- [VulkanEngineSettings.cs:9](Loom.Vulkan/VulkanEngineSettings.cs#L9) — `Planning.md §3`
- [VulkanEngine.cs:88,267](Loom.Vulkan/VulkanEngine.cs#L88) — `§7.4` (readback), `§7.6` (particles) inline leads
- [VulkanMeshStore.cs:15](Loom.Vulkan/VulkanMeshStore.cs#L15) — `Planning.md §12 M5`

### D. Temporal / forward narration *(~20 hits; filter carefully)*

Many hits from "no longer" / "previously" are now legitimate ("module is no longer needed after pipeline creation"). True issues remaining:

- [external/SUIsei/SUIsei/UIPlacement.cs:8](external/SUIsei/SUIsei/UIPlacement.cs#L8) — *"is declared for API shape but lands in MU5"*
- [external/SUIsei/SUIsei/DependencyObject.cs:70](external/SUIsei/SUIsei/DependencyObject.cs#L70) — *"nothing drives it until then"*

### E. Bug archaeology *(3 hits)*

- [DirectX12RenderContext.cs:138-139](Loom.DirectX12/DirectX12RenderContext.cs#L138) — *`(Earlier "identity ingest, no Y-flip" rendered mesh upright but left every fullscreen pass vertically mirrored vs Vulkan…)`* — the parenthetical is pure archaeology. The forward statement (both backends negate the Y row, paired with a negative-height viewport) is already stated in the lines above; drop the `(Earlier…)` parenthetical.
- [VulkanRenderContext.cs:76](Loom.Vulkan/VulkanRenderContext.cs#L76) — *"the old interpolated-string key heap-allocated on every draw"* — explains *why* the value-type key exists, but frames it as a replacement rather than stating the invariant. Rewrite: *"The key is a value type so the per-draw lookup allocates nothing."* (The rest of the sentence already says this; drop the archaeology clause.)
- [VulkanRenderContext.cs:312](Loom.Vulkan/VulkanRenderContext.cs#L312) — *"Value-type, allocation-free replacement for the old interpolated-string pipeline-cache key."* Same fix: drop "replacement for the old interpolated-string pipeline-cache key" — the `PipelineKey` name and the allocation note are enough.

### F. Exception messages + dead scaffold *(cleared ✓)*

All three mileston-flavoured exception messages removed. `NextSlice` dead const deleted.

### G. Gotchas with milestone tags *(mostly cleared ✓)*

The high-value gotchas are intact and tag-free. Remaining:

- [external/SUIsei/SUIsei/DependencyObject.cs:4](external/SUIsei/SUIsei/DependencyObject.cs#L4) and [DependencyProperty.cs:12,47](external/SUIsei/SUIsei/DependencyProperty.cs#L12) — `PlanningUI.md §D1.2` tags still attached to genuinely useful design explanations. Strip the citation; the prose stands on its own.

### H. Redundant "restate-the-code" comments *(unchanged — rare)*

Only the isolated cases noted originally ([DirectX12InstanceUploadBuffer.cs:70](Loom.DirectX12/DirectX12InstanceUploadBuffer.cs#L70), [VulkanRenderContext.cs:223](Loom.Vulkan/VulkanRenderContext.cs#L223)). The overall ratio of "why" to "what" comments remains excellent.

---

## Doc coverage gaps *(3 remain)*

- [VulkanSwapchain.cs:15-21](Loom.Vulkan/VulkanSwapchain.cs#L15) — public `Handle`/`ImageFormat`/`ColorSpace`/`Extent`/`Images`/`ImageViews`/`ImageCount` all bare. *Notable gap.*
- [DirectX12Engine.cs](Loom.DirectX12/DirectX12Engine.cs) `IEngine` surface — `Render`, `Resize`, `WaitIdle`, `CreateMesh`/`CreateTexture`/`CreateMaterial`, `EnableUI`, `GetDebugTextureId`, `SetDebugLines` undocumented while `EnableImGui` beside them is.
- [AssetWatcher.cs:80](Loom.Assets/AssetWatcher.cs#L80) — `Dispose` undocumented.

Previously flagged gaps now closed: `VertexLayout` constructors ✓, `VulkanContext` property block ✓, `LayeredAssetSource` ✓.

(`ToString`/`Equals`/`GetHashCode` overrides being bare is conventional and fine.)

---

## Good comments to preserve

These should survive any further cleanup unchanged (tag-free already):

- [VulkanEngine.cs:864-869](Loom.Vulkan/VulkanEngine.cs#L864) — persistent-resource cross-frame state substitution
- [VulkanEngine.cs:881-891](Loom.Vulkan/VulkanEngine.cs#L881) — aliasing-barrier source-scope widening
- [LifetimePlan.cs:54-57](Loom.Graph/LifetimePlan.cs#L54) — readback lifetime extension
- [GraphSchedule.cs:97-103](Loom.Graph/GraphSchedule.cs#L97) — readers, not the writer, as precise prerequisites (WAR)
- [DirectX12FormatMapping.cs:27-34](Loom.DirectX12/DirectX12FormatMapping.cs#L27) — TYPELESS-depth gotcha
- [DirectX12Technique.cs:193-198](Loom.DirectX12/DirectX12Technique.cs#L193) — one static sampler per sampled input
- [DirectX12Shaders.cs:98-110](Loom.DirectX12/DirectX12Shaders.cs#L98) — structured-buffer `StructureByteStride`/`NumElements` vs root-UAV `GetDimensions()==0` trap
- [DependencyObject.cs:5-9](external/SUIsei/SUIsei/DependencyObject.cs#L5) — DP precedence-ladder summary *(drop the `§D1.2` reference — the prose stands alone)*
- [Geometry.cs:81-93](Loom.Sandbox/Demos/Geometry.cs#L81) — glTF tangent handedness + Gram-Schmidt degenerate-UV guard
- [InstanceUploadBuffer.cs:13-18](Loom.Vulkan/VulkanInstanceUploadBuffer.cs#L13) — "retire, don't free" mid-frame growth

---

## Before → After for remaining work

These are the open fixes. Already-applied examples from the original audit have been removed.

**Stale class sentence (DirectX12)** — [DirectX12RenderContext.cs:23](Loom.DirectX12/DirectX12RenderContext.cs#L23)
```
BEFORE: (vertex/index buffers + an MVP root constant). Compute and ImGui land in following slices.
AFTER:  (vertex/index buffers + an MVP root constant).
```

**Bug archaeology → invariant (DirectX12)** — [DirectX12RenderContext.cs:138](Loom.DirectX12/DirectX12RenderContext.cs#L138)
```
BEFORE: …flip it the same way here. (Earlier "identity ingest, no Y-flip" rendered mesh upright but left
        every fullscreen pass vertically mirrored vs Vulkan — one mesh→fullscreen→backbuffer hop = upside down.)
AFTER:  …flip it the same way here.
```

**Bug archaeology → invariant (Vulkan)** — [VulkanRenderContext.cs:76](Loom.Vulkan/VulkanRenderContext.cs#L76) and [VulkanRenderContext.cs:312](Loom.Vulkan/VulkanRenderContext.cs#L312)
```
BEFORE: …in the render loop) — the old interpolated-string key heap-allocated on every draw.
AFTER:  …in the render loop — the per-draw lookup allocates nothing (zero GC pressure).

BEFORE: // Value-type, allocation-free replacement for the old interpolated-string pipeline-cache key. Distinct
AFTER:  // Value-type, allocation-free pipeline-cache key. Distinct
```

**Gotcha + tag (SUIsei)** — [DependencyObject.cs:4](external/SUIsei/SUIsei/DependencyObject.cs#L4)
```
BEFORE: /// The base of everything that carries dependency properties (PlanningUI.md §D1.2). Holds a sparse…
AFTER:  /// The base of everything that carries dependency properties. Holds a sparse…
```

---

## Recommended remediation (updated)

1. **Fix the one remaining stale comment** — `DirectX12RenderContext.cs:23`: drop the sentence. One line, zero risk.
2. **Bug archaeology (E)** — three targeted rewrites as shown in the gallery above.
3. **SUIsei `§D1.2` tails (C/G)** — strip `(PlanningUI.md §D1.2)` from `DependencyObject.cs:4`, `DependencyProperty.cs:12`, `DependencyProperty.cs:47`, and `(MU4 correctness, PlanningUI.md §D1.2)` from `DependencyObject.cs:70`.
4. **Mechanical B/C/D strip** — the remaining ~247 milestone/section/temporal tags. The pattern is consistent: each is a tail or parenthetical on an otherwise self-contained sentence; amputate the clause. `VulkanEngine.cs` and `VulkanMeshStore.cs` carry the heaviest concentration.
5. **Fill the three doc gaps** — `VulkanSwapchain` properties and the `DirectX12Engine` `IEngine` surface are the ones that matter (public, author-facing).
