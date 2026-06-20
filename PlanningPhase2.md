# Loom — Planning & Design Bible, Phase 2

> **What this is.** `Planning.md` is the Phase 1 design bible: it took Loom from nothing to a declarative render-graph engine that renders a full reference frame on two backends. This document is **Phase 2** — the plan for turning that render-graph-and-effects engine into something you can actually *build a game with*. It is the source of truth for *what* milestones **M11–M16** are; the GitHub board (#6, owner `AtomicBlom`) is their live status; `CLAUDE.md` is the operational layer on top of both.
>
> **The thesis in one line.** Phase 1 built the half of an engine that schedules GPU work; Phase 2 builds the half that feeds it — assets, materials, geometry generalization, animation, and the three different rendering frontiers that three different games demand.
>
> **First Claude Code prompt for this phase:** *"Read Planning.md, then PlanningPhase2.md. We're starting M11 — the asset & material foundation. Expand it from the board and let's go."*

**Status date:** 2026-06-17. Phase 1 (M0–M10) complete; M9 partial. **Phase 2: M11 + M12 + M13 complete** (the shared spine), integrated together on branch `Phase2-spine`. M11 (asset & material foundation) — texture loading + CPU mips, `Material`/multi-texture binding, normal mapping, `Loom.Assets` with image + glTF loaders, normal-map + glTF reference demos at cross-backend parity. M12 (geometry generalization) — per-technique vertex layouts (the canonical `MeshStandard` now carries the M11 tangent, 48 bytes), GPU instancing (CPU-uploaded + compute-filled), an optional `Scene` draw-list with CPU frustum culling, and an instanced-field demo, all at cross-backend parity. M13 (debug & profiling spine) — per-pass GPU timing HUD, generic resource viewer, shader hot-reload (the `Loom.Assets` watcher), render-graph viz export, golden-image regression, and a detached debug camera + frustum freeze, both backends. M14–M16 planned.

---

## 1. Where Phase 1 landed

Loom today is a mature **render-graph + effects** engine. What's shipped and proven:

- **Three-layer architecture intact** (Planning.md §2): Describe (`Loom.Core`) → Execute (`Loom.Graph`) → Bake+record (`Loom.Vulkan`, `Loom.DX12`). Core/Graph carry no graphics-API types.
- **Two backends at pixel parity** — Vulkan (dynamic rendering, sync2, timeline semaphores) and DX12 (fences, PSOs, placed-resource aliasing). Identical graphs + shaders produce byte-identical output (time-driven effects within a small tolerance).
- **Describe layer:** `GraphBuilder`/`PassBuilder` (`.Writes`/`.Blends`/`.Samples`/`.ReadWrites`/`.WritesDepth`/`.ReadsDepth`/`.ReadsVertex`), typed handles (`ColorHandle`/`DepthHandle`/`BufferHandle`), fixed + backbuffer-relative domains, persistent + buffer resources, present/readback. Immutable, hashable, drives the bake cache.
- **Execute layer:** topological scheduling, derived barriers/layout transitions, transient memory aliasing, lifetime analysis — all backend-neutral.
- **Eight shipped effects:** deferred G-buffer + multi-light lighting, single shadow map, SSAO + blur, forward alpha-blend transparency, GPU particles (compute→vertex), JFA outlines + GPU→CPU picking readback, bloom pyramid, auto-exposure + tonemap. Plus HDR (scRGB) output, a low-res world domain + upscale, and an ImGui HUD. `FullReferenceDemo` composes them all (~19 passes).
- **Shaders:** offline Slang → SPIR-V + signed DXIL, reflection-derived descriptor layouts.
- **Tooling:** deterministic headless capture (the sandbox's `--smoke-frames` / `--capture` options + frozen time), validation layers / DX12 info-queue, `RenderGraph.Dump()`/`Hash()`, per-pass GPU debug labels, `Loom.Tests` (TUnit) over the describe/execute layers.

**What it is not.** Loom is not yet an *asset/scene* engine. There is no texture or model loading, no material system, no normal mapping, no instancing, no culling, no sprites/2D, and no animation. Geometry is hand-built `Vertex` (Position/Normal/UV) uploaded once; the user submits one `ctx.Draw` per mesh and owns all culling. That asset/scene/geometry half is the entire subject of Phase 2.

---

## 2. The three target archetypes

Phase 2 is scoped against three deliberately different games. They are not commitments to build games — they are *load cases* chosen to stress the rendering stack in three independent directions, so the engine ends up broadly capable rather than narrowly tuned.

- **A — Pixel metroidvania** with heavy normal-mapped lighting and particle effects. Stresses: **2D content pipeline** + **deferred 2D lighting** + **particles**.
- **B — 3D cartoony** (Harvest Moon / Stardew Valley). Stresses: **skinned characters** + **outdoor sun/day-night** + **stylized lighting** + **foliage instancing**.
- **C — Realistic terraforming**, planet-from-orbit, erosion + factory mechanics. Stresses: **scale**, **precision**, **dynamic geometry**, and **realistic lighting** simultaneously — the heaviest, most research-driven case.

The point of three cases is the overlap: the features all three share become the shared foundation built first; the features unique to one become that game's track.

---

## 3. Per-archetype requirements

Legend: **NEW** = build it · **EXTEND** = generalize something that exists · **HAVE** = usable today.

### 3.A Pixel metroidvania — normal-mapped 2D lighting + particles

*Closest to a playable demo,* because deferred lighting + additive blend + bloom + the low-res world domain + upscale + GPU particles already exist. It mainly needs the 2D content path and normal mapping.

- Texture loading + atlases (NEW); nearest sampling (HAVE), mips optional.
- Sprite / quad batch path (NEW) — instanced/batched quads per atlas+material.
- Tilemap rendering (NEW) — tile-index texture + atlas, or instanced tiles.
- **2D deferred normal-mapped lighting** (EXTEND) — sprite G-buffer (albedo + tangent normal + emissive/mask), many 2D point/spot lights accumulated additively. G-buffer + additive accumulation are HAVE; **normal-map sampling and 2D attenuation are NEW**.
- Generalized particle system (EXTEND) — emitters, lifetime, color-over-life, sub-UV (sprite-sheet) animation. Compute→vertex plumbing is HAVE.
- Sprite-sheet animation (NEW, lightweight) — UV sub-rect over time.
- Parallax layers + 2D draw ordering (EXTEND).
- Bloom / glow, pixelation, upscale — **HAVE**.

**Debug:** G-buffer channel viewer (catches the flipped-green-normal bug), light overlay (positions/radii/falloff), normal-as-RGB, atlas/pivot/overdraw heatmap, sprite + shader hot-reload.

### 3.B 3D cartoony (Stardew / Harvest Moon)

A moderate lift on the shared foundation: chiefly skinned characters, cascaded sun shadows, a day/night sky. Stylized lighting is *less* work than PBR.

- glTF model loading (NEW).
- **Skeletal animation / skinning** (NEW, large) — extended vertex layout (bones + weights), clips, sampling/blending, GPU skinning.
- Material + normal mapping + toon/ramp lighting (EXTEND).
- **Cascaded shadow maps** (EXTEND) — single 1024² map is insufficient outdoors.
- Sky + day/night cycle + dynamic ambient (NEW).
- Foliage / crop instancing + wind (NEW); alpha-test cutout (EXTEND).
- Color grading / season LUT (EXTEND); SSAO, bloom/tonemap, JFA selection (HAVE).

**Debug:** skeleton/bone viz + clip scrubber, CSM cascade visualization (tint by cascade, splits, texel density), time-of-day scrubber, frustum-cull debug, draw-call/instance stats.

### 3.C Realistic terraforming, planet-from-orbit + factory

The heaviest — effectively several milestones. It stresses scale, precision, dynamic geometry, and realistic lighting at once.

- PBR materials + IBL / reflection probes (NEW).
- Atmospheric scattering + sky from orbit & ground (NEW).
- Planetary / clipmap terrain + continuous LOD (NEW, large).
- **Dynamic / deformable terrain** for erosion + terraforming (NEW) — GPU compute erosion sim feeding displacement / re-meshing via indirect draw (the GPU-particle compute path is the conceptual seed).
- **GPU-driven rendering** (NEW, large) — instancing + indirect + GPU frustum + Hi-Z occlusion culling, for tens of thousands of factory objects.
- Virtual texturing / streaming (NEW); floating origin / camera-relative rendering (NEW).
- CSM over huge range (EXTEND); water + SSR (NEW; SSR is listed-but-uncoded); TAA and/or MSAA resolve (NEW; both deferred today); HDR + auto-exposure (HAVE); volumetric clouds/fog + some GI (NEW).

**Debug:** GPU profiler / pass timing + VRAM budget, GPU-driven culling debug (Hi-Z viewer, drawn/culled counts), LOD visualization, virtual-texture debug (resident pages, feedback), terrain/erosion field overlays (height/water/flow/sediment/slope), PBR G-buffer channel viewer.

---

## 4. Shared foundations vs per-track

**Shared foundations — all three need these; build first:**
1. **Asset I/O** — texture loading + mips (all 3); glTF model loading (B, C; A optional).
2. **Material / multi-texture binding per draw** — reflection already derives layouts; need the binding abstraction.
3. **Normal mapping in the lighting model** — A, B, *and* C all want it.
4. **Vertex-layout generalization + instancing/indirect** — unlocks skinning (B/C), sprites (A), foliage (B), factory scale (C).
5. **`Scene` draw-list abstraction + optional CPU frustum culling** — keeps the "you hand me draw lists" invariant; engine *offers* culling, doesn't force it.
6. **Cascaded shadow maps** — shared by B and C.

**Shared debug spine — accelerates all three:** resource/G-buffer channel viewer; per-pass GPU timing + stats HUD; shader/asset hot-reload; render-graph viz export; golden-image regression; detached debug camera + frustum freeze.

**Unique per track:** A → sprites/tilemaps/2D-lighting/sub-UV particles; B → skinning/CSM/sky-day-night/foliage-wind/toon; C → PBR+IBL/atmosphere/planet-terrain/GPU-driven/virtual-texturing/floating-origin/water/TAA/erosion-sim.

---

## 5. The milestone roadmap (M11–M16)

M10 (DX12) is the last shipped milestone. Build the **shared spine first** (M11→M12→M13), then branch by archetype (M14/M15/M16). Every milestone must land in **both** backends (or be `<Use…>`-gated), and instancing/indirect/GPU-culling must be authored as **neutral describe-layer intents** — no graphics-API type may enter Core/Graph. Each milestone's task cards live on the board; the checklists below are the deliverable definitions.

### M11 — Asset & material foundation *(unblocks all 3)* ✅ **SHIPPED**
Texture loading + mips · material / multi-texture technique inputs · **normal mapping** in the deferred lighting model · reference demo + cross-backend golden image.
- *Shipped:* `Loom.Core` gains `TextureData` (CPU box-filter mip chain), `Texture`/`Material` handles, `SamplerAddress`, a tangent in `Vertex`, and `CreateTexture`/`CreateMaterial` + `Draw(mesh, material, technique)`. `Loom.Assets` (new neutral project) hosts the StbImageSharp image loader and the SharpGLTF model loader. Both backends gain a `TextureStore` (multi-mip upload) + `MaterialStore` + material-draw binding + sampler-address threading. `gbuffer_normalmap.slang` does tangent-space normal mapping into the existing deferred G-buffer. `NormalMappingDemo` (procedural + committed brick PNGs) and `GltfDemo` (committed `cube.gltf`) render at cross-backend parity (Vulkan↔DX12, within the established tolerance). Decisions taken: StbImageSharp; `Loom.Assets` project; glTF (SharpGLTF) **included now**. *(Also fixed a pre-existing DX12 break: `ssao_blur` used `Sampler2D.GetDimensions`, which Slang lowers to a form dxc rejects — switched to the `ddx`/`ddy` uv gradient.)*
- *Tasks:* TextureStore (decode + GPU upload + mips, per backend) · Material / multi-texture binding (Core) · normal mapping in `gbuffer.slang`/`lighting.slang` · normal-map reference demo.
- *Decision:* image-decode library (StbImageSharp vs ImageSharp) + whether glTF (SharpGLTF) lands now or in M15.
- *Touches:* new `TextureStore` mirroring `MeshStore`; a `Material` abstraction in `Loom.Core`; the deferred shaders.
- *Unlocks:* A's lighting signature, B/C surfaces.

### M12 — Geometry generalization *(unblocks all 3; critical for B/C scale)* — **IMPLEMENTED**
Per-technique vertex layouts · GPU instancing · `Scene` draw-list abstraction with bounds · optional CPU frustum culling · instanced-field demo with cull stats. *(Indirect draw was deferred to M16 — see below.)*
- *Decision (resolved):* vertex-layout & instancing API shape — **explicit `VertexLayout`** (declared attribute set → packed offsets/stride), not reflection-derived: it is the natural output of an asset importer (glTF defines its attribute set independently of any shader), needs the least machinery on both backends, and makes the per-vertex/per-instance stream split unambiguous. The instance buffer is modeled like the existing `.ReadsVertex` particle path. `VertexLayout.MeshStandard` preserves the canonical pos/normal/uv so every prior demo is unchanged.
- *Decision (resolved):* **indirect draw deferred to M16.** M12's cull demo uses CPU frustum culling (CPU-known count), so it needs no indirect path; the GPU-count indirect path (args buffer from compute + `DrawIndirect` + DX12 command-signature/`ExecuteIndirect`) lands with its real consumer, GPU-driven culling, in M16.
- *Touches:* `Vertex`/`Mesh`/`MeshStore`, `TechniqueDescriptor`, `IRenderContext` (instanced/indirect verbs), new `Loom.Core/Scene.cs`, `Camera`.

```csharp
// Direction (sketch, not final): instancing follows the particle pattern —
// an instance buffer is just a BufferHandle the draw reads, so the graph
// derives the compute→draw barrier exactly as it does for GPU particles.
g.Compute("place").ReadWrites(instances);          // GPU fills transforms
g.Pass("foliage").Writes(gbuffer)
    .Draw(ctx => ctx.DrawInstanced(grassMesh, "foliage", instances, count));
```

### M13 — Debug & profiling spine *(serves all; pull forward)*
Resource / G-buffer channel viewer · per-pass GPU timing (timestamp queries) + stats HUD · shader/asset hot-reload · render-graph viz export (GraphViz/JSON) · golden-image regression in CI · detached debug camera + frustum freeze.
- The mechanism for several of these already exists (named handles for the viewer, `DebugLabelScopes` for timing, the capture harness for regression, `RenderGraph.Dump()` for export) — M13 builds the UI/automation on top.
- *Touches:* `ImGuiRenderer` (both backends), timestamp query pools, `RenderGraph.cs` + the Execute-layer plans, `Loom.Tests`.

### M14 — Track A: 2D / pixel pipeline
Sprite batching + atlases · tilemaps · **2D deferred normal-mapped lighting** (many 2D lights) · generalized particle system (emitters, sub-UV, color-over-life) · sprite-sheet animation · parallax + 2D ordering · 2D debug overlays · pixel-art reference demo. Reuses M11 normal mapping + existing bloom/pixelation/upscale. **Closest to a playable demo.**

### M15 — Track B: stylized 3D
glTF loading · **skeletal animation / GPU skinning** · **cascaded shadow maps** · sky + day/night + dynamic ambient · foliage instancing + wind (+ alpha-test) · toon/ramp lighting + color grading · skeleton/cascade/time-of-day debug · stylized reference demo.
- *Decision:* skinning approach (compute pre-skin into a transient buffer reused across passes vs per-pass vertex-shader skinning) + glTF library.

### M16 — Track C: large-world realistic *(umbrella — split into sub-milestones when started)*
PBR + IBL/probes → atmospheric scattering + sky → planetary/clipmap terrain + continuous LOD → dynamic deformable terrain (GPU erosion sim) → **GPU-driven rendering** (indirect + Hi-Z occlusion) → virtual texturing/streaming → floating origin → water + SSR → TAA/MSAA resolve → volumetric clouds/fog + GI → the matching debug viewers → realistic reference demo.
- *Decisions:* terrain representation (heightmap clipmap/CDLOD vs voxel/SDF — driven by whether caves/overhangs/digging are required) · GPU-driven culling strategy + large-world precision (floating origin).

### Effort read & sequencing
- **A** is closest to shippable on today's foundation (mostly M11 + M14).
- **B** is a moderate lift (M11–M13 + skinning/CSM/sky in M15).
- **C** is a multi-milestone research program.

Regardless of which game is pursued, **M11–M13 is the obvious next step** — pure shared leverage, and it includes the debug tooling that makes everything after it faster.

---

## 6. Design principles carried into Phase 2

The Planning.md §8 invariants still hold and now apply to a wider surface:

- **Derived, not declared.** Instancing, indirect draws, and GPU culling are *intents* in the describe layer; the engine still derives order and barriers. An instance/indirect buffer is a resource with edges — never a manual ordering knob.
- **You hand me draw lists.** Culling stays *optional and app-owned*. The engine may **offer** a frustum-cull helper and a `Scene` container, but it must not mandate a scene graph or seize draw submission.
- **Backend isolation is now load-bearing in practice.** Two backends exist. Every Phase 2 feature — texture upload, skinning, instancing, Hi-Z culling, virtual texturing — must land in (or be gated for) **both** Vulkan and DX12, with zero graphics-API types leaking into Core/Graph.
- **Pure per-frame closures.** New per-frame data (skinning matrices, instance transforms, time-of-day) is pulled from `ctx` / `FrameInputs`, never captured.
- **Ping-pong via handle swap** extends naturally to erosion fields, JFA-style 2D effects, and TAA history.

**New Phase 2 principles:**
- **Assets are app/Loom.Assets-owned, not Core-owned.** Loaders (image, glTF) produce neutral descriptions; Core never gains a decode dependency or an API type.
- **Every feature ships with its debug view.** A G-buffer channel, a cascade tint, a culled-count, an erosion field — if you can't see it, you can't debug it. The debug view is part of the deliverable, not a follow-up.

---

## 7. Open decisions for Phase 2

Recorded here and on the board as `DECIDE`/`DECISION` cards; resolve each before the implementation it gates.

| # | Decision | Gates | Leaning |
|---|---|---|---|
| 1 | Image-decode library (StbImageSharp / ImageSharp / custom) | M11 | StbImageSharp (light, MIT, no native deps) |
| 2 | glTF library + when (SharpGLTF; M11 or M15) | M11/M15 | SharpGLTF, in M15 unless C needs meshes sooner |
| 3 | Vertex-layout + instancing API shape | M12 | **Resolved:** explicit `VertexLayout` (declared, not reflection-derived) — importer-friendly, least cross-backend machinery; instance buffer modeled like the particle `.ReadsVertex` path. Indirect draw deferred to M16. |
| 4 | Skinning approach (compute pre-skin vs VS skinning) | M15 | Compute pre-skin into a transient buffer (reused by depth + g-buffer) |
| 5 | Terrain representation (clipmap heightfield vs voxel/SDF) | M16 | Decide on caves/overhangs/digging need |
| 6 | GPU-driven culling + large-world precision (floating origin) | M16 | Indirect + Hi-Z; camera-relative transforms |

---

## 8. Solution-layout additions

Phase 1 layout (Planning.md §11) gains:
- `Loom.Assets` (proposed) — image + glTF loaders producing neutral descriptions; **no graphics-API reference**. (Alternatively these live app-side in `Loom.Sandbox` until a second consumer appears.)
- `Loom.Vulkan` / `Loom.DX12` each gain a `TextureStore` (mirroring `MeshStore`), and instancing/indirect + (later) GPU-culling/virtual-texturing machinery.
- `Loom.Core` gains `Material`, `Scene`, generalized vertex layouts, and animation/skeleton types.
- `Loom.Tests` gains golden-image regression (M13).

The dependency rule is unchanged: `Sandbox → {Vulkan, DX12} → Graph → Core`, with `Loom.Assets` (if created) sitting beside Core as a neutral producer.

---

## 9. Verification

Phase 2 verifies the same way Phase 1 proved every effect:
1. Each feature gets a focused `Loom.Sandbox` demo.
2. `--capture` + frozen time → a deterministic frame.
3. **Cross-backend pixel diff** — Vulkan vs DX12 must match within the established tolerance.
4. Golden-image regression (formalized in M13) guards against drift.
5. Validation layers / DX12 info-queue stay clean.

The M13 channel viewer + GPU timing exist specifically to make this loop fast for the heavier M15/M16 features.

---

## 10. Working in parallel (three worktrees)

The three game tracks are **not independent**: M14, M15, and M16 all depend on the shared spine **M11–M13** (textures, materials, normal mapping, vertex layouts, instancing, debug tooling). For parallel worktrees:

- **Land M11–M13 first** (one worktree, or a shared first pass) — then A/B/C fan out cleanly.
- If three streams start immediately, expect each to re-derive parts of the foundation; coordinate so the shared work isn't built three times.
- Each card body names its dependencies explicitly; honor them when picking up work.

---

*Phase 1 source of truth: `Planning.md`. Phase 2 milestone status: GitHub board #6. Operational guidance: `CLAUDE.md`.*
