# Loom — Comment & Documentation Review

**Date:** 2026-06-20
**Method:** Mechanical sweep (grep) for milestone/section/narration tells across all `.cs`, then 6 parallel reviewers (one per project cluster) judging what grep can't: public-API doc coverage, non-self-contained prose, stale comments, and concrete rewrites.
**Standard applied (the owner's):** A comment should describe **what the code does / why**, and be **self-contained** — a reader should never have to go look up a milestone or a planning-doc section to understand it. Public APIs should be *well documented*. Notes guiding future development, and explanations of a complex algorithm or gotcha, are welcome — as long as they stand on their own.

---

## Headline

**The documentation is substantively excellent and nearly complete — and that is the problem.** Coverage runs ~90–100% across every project, the prose genuinely explains *why* rather than restating code, and the backends carry some of the best GPU "why" comments you'll find anywhere. But almost every comment — **including public `///` XML docs that ship to consumers via IntelliSense** — is barnacled with the exact "AI-generated giveaway" you described: it narrates the *context in which it was written* rather than standing on its own.

Three things make this worth a dedicated cleanup:

1. It's **pervasive and public**: ~245 `Planning.md`/`§`-section references and a comparable volume of milestone tags (`M0`–`M16`, `MU0`–`MU5`, `Phase A/B/C`, `M7-B4`, `M14-6`) saturate the codebase, heavily concentrated in public doc comments.
2. Some of it is **not just style — it's now factually wrong**: comments that narrated "this throws / is a no-op / derives this later" have gone stale as the code caught up, so they actively mislead.
3. The fix is mostly **mechanical and low-risk** — the offending clause is usually a *tail* or a *parenthetical* bolted onto an otherwise self-contained sentence. Amputate the clause, keep the explanation.

This is also a leading indicator worth acting on *now* because two agents are actively writing M15/MU1 code in the same house style — without a stated convention, they'll keep minting more of it.

---

## Categories (worst → least)

### A. Stale narration that now contradicts the code  ⬅ *fix first — actively misleading*

These describe a past or planned state that the code has since moved past. A reader trusts them and is wrong.

- [DirectX12Engine.cs:18-26](Loom.DirectX12/DirectX12Engine.cs#L18) — class summary: *"The graph-driven members therefore throw `NotSupportedException` for now — they land in the following slice…"*. The graph path (`RenderGraphFrame`) is **fully implemented in this same file**. The summary describes a bootstrap that no longer exists.
- [DirectX12RenderContext.cs:21](Loom.DirectX12/DirectX12RenderContext.cs#L21) — *"Compute and ImGui land in following slices."* Both are implemented here.
- [Technique.cs:74](Loom.Vulkan/Technique.cs#L74) (Vulkan) — *"Hardcoded shape; SPIR-V reflection derives this later."* `ShaderReflection.FromBytes` already derives it.
- [ResourceState.cs:8](Loom.Graph/ResourceState.cs#L8) — *"…lets a **future** DirectX12 backend reinterpret the same transitions"*. DirectX12 shipped. (Same stale "future DirectX12 / Vulkan today, DirectX12 later" framing in [BarrierPlan.cs:9](Loom.Graph/BarrierPlan.cs#L9), [PassBarriers.cs:10](Loom.Graph/PassBarriers.cs#L10).)
- [DirectX12Shaders.cs:16](Loom.DirectX12/DirectX12Shaders.cs#L16) — summary frames FXC/HLSL as the path and "signed-DXIL via runtime DXC is the follow-up," but `TryResolveStageDxil` shows signed DXIL is already primary.
- [IRenderContext.cs:5-13](Loom.Core/IRenderContext.cs#L5) — the most-read interface in the layer opens with a roadmap paragraph ("M3 adds only… until the Technique builder lands (M5)…").

### B. Milestone & phase references (the core giveaway — pervasive)

`// M13 resource viewer`, `(M14-4 generalized particles)`, `MU0 Phase A`, `M7-B5`, `lands in MU2`. A reader has to know what "M13" *was* to parse them. Concentrated in:
- **Demo & test class summaries** ([Loom.Sandbox/Demos/](Loom.Sandbox/Demos/), [Loom.Tests/](Loom.Tests/), [Loom.UI.Tests/](Loom.UI.Tests/)) — these systematically *open* with the tag: `/// Sprite batching + texture atlases (PlanningPhase2.md M14-1, Track A)…`. The tag is the first thing the reader sees, before any description.
- **The host** ([Program.cs](Loom.Sandbox/Program.cs)) — ~16 inline `// M13 …` / `// MU0 Phase A …` leads.
- **The whole UI track** — every control doc and every DP-rung doc ends in `(MU2)` / `— MU3.` / `lands in MU4`.
- **Both backends + Core** — milestone tags threaded through type and member docs.

*(Note: a portion of raw grep hits like `m.M11`/`m.M22` in [Bounds.cs](Loom.Core/Bounds.cs) and [Matrix4x4FrustumExtensions.cs](Loom.Core/Matrix4x4FrustumExtensions.cs) are legitimate matrix elements, not milestone refs — excluded.)*

### C. Planning-doc / `§`-section references ("go look it up")

~245 occurrences. `(Planning.md §8)`, `(PlanningUI.md §D1.2)`, `(§12 M4)`, bare `§7.6.` / `§7.4.` dangling at the end of a sentence. These force the reader to a document they cannot see from the call site or from IntelliSense. The surrounding prose almost always already states the actual content — the cite is a redundant footnote that can simply be deleted.

### D. Temporal / forward narration: "for now", "lands in", "arrives in", "until then", "later optimisation"

The non-milestone cousin of B. Examples: [DescriptorAllocator.cs:10](Loom.Vulkan/DescriptorAllocator.cs#L10) *"a later optimisation"*; [TechniqueCache.cs:10](Loom.Vulkan/TechniqueCache.cs#L10) *"In-memory for now; … will key a persistent VkPipelineCache later"*; [TextBlock.cs:25](Loom.UI/Controls/TextBlock.cs#L25) *"(consumed once glyph composition lands in MU1)"*; [GraphBuilder.cs:127](Loom.Core/GraphBuilder.cs#L127) *"anchor them to the backbuffer domain for now"*.

### E. Bug archaeology / commit-style history in comments

A comment that recounts a fixed bug belongs in the commit message, not the source.
- [TransientResources.cs:265-270](Loom.Vulkan/TransientResources.cs#L265) — a full changelog entry: *"This branch **previously returned** `_swapExtent` unconditionally, dropping `resource.Scale` … DirectX12's DirectX12Transients **had this right** via …"*. The forward statement (each backbuffer-domain resource scales by its own `Scale`) is valuable; the archaeology is not.
- [RenderContext.cs:570-571](Loom.Vulkan/RenderContext.cs#L570) — *"…replacement for **the old** interpolated-string pipeline-cache key."*
- [DirectX12RenderContext.cs:137](Loom.DirectX12/DirectX12RenderContext.cs#L137) — *"(Earlier 'identity ingest, no Y-flip' rendered mesh upright but…)"*.

### F. Context-assuming exception messages + dead scaffold

User-facing strings that assume milestone vocabulary:
- [DirectX12RenderContext.cs:272](Loom.DirectX12/DirectX12RenderContext.cs#L272) — `"DirectX12: sampling the backbuffer or a buffer resource lands in a later slice."` — a user hitting this can't act on "a later slice."
- [DirectX12RenderContext.cs:302](Loom.DirectX12/DirectX12RenderContext.cs#L302) — `"…multi-storage compute lands later."`
- [DirectX12RenderContext.cs:25](Loom.DirectX12/DirectX12RenderContext.cs#L25) — `private const string NextSlice = "…lands in a following executor slice."` — **dead** (no references); delete.
  *(Contrast the good ones in the same file: [DirectX12Engine.cs:884](Loom.DirectX12/DirectX12Engine.cs#L884) `"…has no '<name>.cs.dxil'… and no built-in stand-in."` — standalone and actionable.)*

### G. Gotchas welded to a milestone/section tag  — *strip the tag, KEEP the comment*

The highest-care category: a genuinely valuable explanation with an external reference embedded. Do **not** delete these — remove only the tag.
- [Swapchain.cs:127-131](Loom.Vulkan/Swapchain.cs#L127) — the scRGB linear-transfer HDR rationale, tagged `(M7-A7)`/`(M7-B6)`.
- [RenderContext.cs:454-457](Loom.Vulkan/RenderContext.cs#L454) — Vulkan +Y-down clip-space explanation, tagged `(M7-B5)`.
- [Technique.cs:201-205](Loom.Vulkan/Technique.cs#L201) — the legacy-float4 vs layout-driven particle-stride explanation, tagged `(M14-4)` + `§7.6`.
- [GltfLoader.cs:29-31](Loom.Assets/GltfLoader.cs#L29) — why non-triangle primitives are skipped, prefixed `M11`.
- [DirectX12ImGuiRenderer.cs](Loom.DirectX12/DirectX12ImGuiRenderer.cs) / [ImGuiRenderer.cs:68](Loom.Vulkan/ImGuiRenderer.cs#L68) — the win-arm64 by-value-`ImVec2` marshalling no-op (a real platform gotcha), repeated 4× each carrying `(M13)`.

### H. Redundant "restate-the-code" comments — *rare; the codebase is good here*

Only isolated cases ([DirectX12InstanceUploadBuffer.cs:70](Loom.DirectX12/DirectX12InstanceUploadBuffer.cs#L70) `// 50% headroom` next to `needed + needed/2`; [RenderContext.cs:223](Loom.Vulkan/RenderContext.cs#L223)). Worth noting precisely *because* the codebase otherwise favors "why" over "what" — that instinct is good and should be preserved.

---

## The opposite problem: genuine doc-coverage gaps

The owner wants public APIs well documented; these public members have *no* `///`:

- [VertexLayout.cs:37,40](Loom.Core/VertexLayout.cs#L37) — both **public constructors** (the type authors call). *Notable gap.*
- [Swapchain.cs:15-21](Loom.Vulkan/Swapchain.cs#L15) — public `Handle`/`ImageFormat`/`ColorSpace`/`Extent`/`Images`/`ImageViews`/`ImageCount`.
- [Technique.cs:20-21](Loom.Vulkan/Technique.cs#L20) — public `Pipeline`/`Layout`.
- [VulkanContext.cs:29-40](Loom.Vulkan/VulkanContext.cs#L29) — the public property block (`Vk`/`Instance`/`Device`/queues/families/…).
- [DirectX12Engine.cs](Loom.DirectX12/DirectX12Engine.cs) `IEngine` surface — `Render`, `Resize`, `WaitIdle`, `CreateMesh`/`CreateTexture`/`CreateMaterial`, `EnableUI`, `GetDebugTextureId`, `SetDebugLines` are bare while `EnableImGui` right beside them is documented (jarring asymmetry).
- [LayeredAssetSource.cs:17,19](Loom.Assets/LayeredAssetSource.cs#L17) `Roots`/`TryResolve`; [AssetWatcher.cs:80](Loom.Assets/AssetWatcher.cs#L80) `Dispose`.

(`Loom.Core`, `Loom.Graph`, and `Loom.UI` are otherwise ~95–100% covered. `ToString`/`Equals`/`GetHashCode` overrides being bare is conventional and fine.)

---

## Good comments to preserve (do not strip in the cleanup)

The cleanup must be surgical — these are the high-water mark and should survive (tag-stripped where noted):

- [VulkanEngine.cs:864-869](Loom.Vulkan/VulkanEngine.cs#L864) — persistent-resource cross-frame state substitution (why per-frame `Undefined` would discard exposure history). Tag-free, exemplary.
- [VulkanEngine.cs:881-891](Loom.Vulkan/VulkanEngine.cs#L881) — aliasing-barrier source-scope widening ("the fresh-memory TopOfPipe source is unsafe — predecessor GPU work may still be running").
- [LifetimePlan.cs:54-57](Loom.Graph/LifetimePlan.cs#L54) — readback lifetime extension (aliaser must not recycle a readback buffer before the end-of-frame copy → garbage picking / flickering outline). *(Drop the trailing `§7.4`.)*
- [GraphSchedule.cs:97-103](Loom.Graph/GraphSchedule.cs#L97) — why the *readers*, not the writer, are a resource's precise prerequisites (WAR).
- [DirectX12FormatMapping.cs:27-34](Loom.DirectX12/DirectX12FormatMapping.cs#L27) — the TYPELESS-depth (typed DSV + typed SRV) gotcha.
- [DirectX12Technique.cs:193-198](Loom.DirectX12/DirectX12Technique.cs#L193) — one static sampler *per* sampled input, with the exact PSO-creation failure if you share `s0`.
- [DirectX12Shaders.cs:98-110](Loom.DirectX12/DirectX12Shaders.cs#L98) — structured-buffer `StructureByteStride`/`NumElements` vs root-UAV `GetDimensions()==0` trap.
- [DependencyObject.cs:5-9](Loom.UI/DependencyObject.cs#L5) — the DP precedence-ladder summary (`Animation > Local > Style > Inherited > Default`, "fire callback only on a *resolved* change"). *(Drop the `§D1.2` tail.)*
- [Geometry.cs:81-93](Loom.Sandbox/Demos/Geometry.cs#L81) — glTF tangent handedness + Gram-Schmidt degenerate-UV guard.
- [InstanceUploadBuffer.cs:13-18](Loom.Vulkan/InstanceUploadBuffer.cs#L13) — "retire, don't free" mid-frame growth (a draw recorded earlier still references the old buffer).

---

## Before → After rewrite gallery

Representative fixes spanning every layer. The pattern is consistent: keep the load-bearing content, drop the external anchor.

**Public interface doc (Core)** — [IRenderContext.cs:5-13](Loom.Core/IRenderContext.cs#L5)
```
BEFORE: …The full surface (ctx.Scene, ctx.Camera, … Planning.md §5) lands as the milestones that need it
        arrive… M3 adds only the fullscreen-pass verbs… Techniques are addressed by name until the
        describe-layer Technique builder lands (M5); the strings here are that placeholder.
AFTER:  …Techniques are addressed by logical name (the strings passed to FullScreen(string), Draw(Mesh, string),
        etc.); the engine resolves each name to a registered TechniqueDescriptor.
```

**Public setting doc (Core)** — [EngineSettings.cs:32-37](Loom.Core/EngineSettings.cs#L32)
```
BEFORE: …falling back to SDR otherwise. The HDR output path lands across M7-A7/B6; until then this is
        recorded but inert.
AFTER:  …falling back to SDR otherwise. (If still true: "A backend that has not implemented the HDR path
        treats this as SDR." — a capability fact, not a schedule.)
```

**Stale class summary (DirectX12)** — [DirectX12Engine.cs:18-26](Loom.DirectX12/DirectX12Engine.cs#L18)
```
BEFORE: …This first slice mirrors how the Vulkan backend started (M0/M1)… The graph-driven members therefore
        throw NotSupportedException for now — they land in the following slice…
AFTER:  …When a graph is set, Render() runs the executor (GraphSchedule/BarrierPlan/AliasPlan consumed here as
        backend-neutral products); with no graph it falls back to a fixed clear + hand-baked triangle.
```

**Exception message (DirectX12)** — [DirectX12RenderContext.cs:272](Loom.DirectX12/DirectX12RenderContext.cs#L272)
```
BEFORE: "DirectX12: sampling the backbuffer or a buffer resource lands in a later slice."
AFTER:  "DirectX12: only transient color/depth targets can be sampled; sampling the backbuffer or a buffer resource
         is not supported."
```

**Gotcha + tag (Vulkan)** — [Swapchain.cs:127-131](Loom.Vulkan/Swapchain.cs#L127)
```
BEFORE: // HDR output (M7-A7): negotiate scRGB (linear FP16)… the only HDR-specific change is the tone-map
        // preserving the extended range (M7-B6)…
AFTER:  // HDR output: negotiate scRGB (linear FP16)… the only HDR-specific change is the tone-map preserving
        // the extended range…
```

**Bug archaeology → invariant (Vulkan)** — [TransientResources.cs:265-270](Loom.Vulkan/TransientResources.cs#L265)
```
BEFORE: …(This branch previously returned _swapExtent unconditionally, dropping resource.Scale — so the bloom
        mips were created FULL-resolution… DirectX12's DirectX12Transients had this right…)
AFTER:  …Dropping resource.Scale here would make the bloom "downsample" chain a 1:1 copy — no glow spread,
        no halo.
```

**Public property doc (UI)** — [TextBlock.cs:25](Loom.UI/Controls/TextBlock.cs#L25)
```
BEFORE: /// <summary>Glyph colour (consumed once glyph composition lands in MU1).</summary>
AFTER:  /// <summary>Glyph colour. Not yet rendered (text emits no glyph content).</summary>
```

**Demo summary (Sandbox)** — [PixelArtDemo.cs:8-9](Loom.Sandbox/Demos/PixelArtDemo.cs#L8)
```
BEFORE: /// The Track-A pixel-art reference demo (PlanningPhase2.md M14-8) — the capstone that composes the whole
        /// 2D pipeline: a parallax background (M14-6), a tile-index tilemap ground (M14-2), animated sprites
        /// (M14-5), dynamic normal-mapped 2D point lights (M14-3), and generalized GPU ember particles (M14-4).
AFTER:  /// The pixel-art reference scene — every 2D capability composed into one frame: a scrolling parallax
        /// background, a tile-index tilemap ground, animated sprites, dynamic normal-mapped 2D point lights,
        /// and GPU ember particles.
```

**Cross-demo inline ref (Sandbox)** — [TilemapDemo.cs:96](Loom.Sandbox/Demos/TilemapDemo.cs#L96)
```
BEFORE: // Reuse the M14-1 fixed-key-light compose (the focus here is the tilemap; M14-8 composes the real lights).
AFTER:  // Light with the shared fixed-key-light compose pass; the focus here is the tilemap geometry, not the lighting.
```

**Test summary** — [GoldenImageTests.cs:3-4](Loom.Tests/GoldenImageTests.cs#L3)
```
BEFORE: /// M13 golden-image regression. Opt-in: it compares freshly captured frames (in LOOM_GOLDEN_DIR)…
AFTER:  /// Golden-image regression: compares freshly captured frames (in LOOM_GOLDEN_DIR) against committed
        /// baselines under Goldens/, so it is SKIPPED in the no-GPU CI run…
```

---

## Recommended remediation

1. **Fix Category A (stale) first** — it's small and the comments are actively wrong, so it's the only part that risks misleading a reader (or one of the in-flight agents) *today*.
2. **Semi-mechanical strip for B/C/D** — bare `(M…)`, `(Planning*.md §…)`, dangling `§…`, and trailing `(MUn)` clauses can be removed with review; the sentence usually reads correctly without them. This is the bulk by volume.
3. **Manual, careful pass for E/F/G** — bug archaeology, exception messages, and gotcha-welded-to-tag need a human/considered rewrite (examples above). The dead `NextSlice` const just gets deleted.
4. **Fill the doc gaps** — the `VertexLayout` constructors and the `DirectX12Engine` `IEngine` members are the ones that matter (public, author-facing).
5. **Add a one-line comment convention to `CLAUDE.md`** so new code (and the M15/MU1 agents) stops generating the pattern. Suggested wording:

   > **Comments are self-contained.** Describe what the code does or why; never require the reader to look up a milestone, phase, or planning-doc section to understand a comment. Don't reference `Mxx`/`MUx`/`Phase x`/`§…` or narrate project history ("for now", "previously", "lands in…") in source — that belongs in commit messages / the board. Document public APIs. Keep comments that explain a non-obvious algorithm, gotcha, or "why this not that."

This convention is itself the durable fix; the cleanup pass is the one-time catch-up.
