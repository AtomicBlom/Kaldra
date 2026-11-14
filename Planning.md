# Loom — Design Bible & Roadmap

> **What this is.** The forward-looking design bible: the engine's core idea, its architecture, the Pillars of Design every change must obey, the backend-portability posture, and **the work that remains**. It deliberately does *not* catalogue what is already built — see [`README.md`](README.md) for the implemented-feature list and how to use the API. `CLAUDE.md` is the operational layer (how the code is written, where the project is now). The GitHub Projects board (#6, owner `AtomicBlom`) is the live task status.
>
> **How the remaining work decomposes.** The render-graph core, the reference effects, both backends at pixel parity, and the asset / material / geometry / skinning / debug spine are all implemented (README). What's left is *not* "three game demos." It splits cleanly into **a short list of genuine engine-capability gaps (§5.1) and a large body of shader/buffer/content authoring the existing engine already supports (§5.2).** That split is the heart of this document.

---

## 1. The core idea

A .NET engine that lets you *fluently and declaratively* compose a reusable rendering pipeline, where synchronization, timing, uploads, and resource lifetimes are handled for you.

The hurdle that historically blocks this: graphics APIs want to be called procedurally, with massive blocks of structs everywhere, fighting any clean object tree. **The reframe that dissolves it:** the procedural, struct-heavy nature is a property of the *backend*, not the *frontend*. `vkCreate*` / D3D12 create calls **copy** their inputs — so the managed object tree can be the clean, GC'd thing we want, and the struct soup need only *transiently exist*, pinned in native memory for one create call, then gone. That marshaling step is the **seam**; all procedural ugliness lives inside it.

**The one principle everything follows from:** you never describe *when* or *in what order* — only *what each pass reads and writes*. Ordering, barriers, layout transitions, memory aliasing, and queue synchronization are all *derived* from the dependency edges. New capabilities enter as new *resource kinds* or new *edge directions*, and the schedule falls out every time. The vocabulary never grows past "declare a resource, declare a read/write intent."

A loom is not told the order to lay each thread. You warp it, declare the pattern, and the cloth falls out of the structure. Passes are threads; a frame is the cloth.

---

## 2. Architecture: Describe → Bake → Execute

Three layers, mapped to projects, kept strictly separate. This separation is what keeps the design manageable.

- **Describe** (`Loom.Core`) — immutable, hashable, fluent builders (`GraphBuilder`, `PassBuilder`, technique/vertex-layout builders, typed handles, `Domain`). The only layer authored against; allocates freely; hashability drives the bake cache.
- **Execute** (`Loom.Graph`) — per frame: topological sort from the read/write edges, then *derive* barriers / layout transitions / aliasing and drive command recording. Produces **backend-neutral** decisions (`GraphSchedule`, `BarrierPlan`, `AliasPlan`, `LifetimePlan`, `AttachmentAction`) — no graphics-API types.
- **Bake + record** (the backends) — the marshaling seam: flatten descriptions to native structs, create pipelines/descriptor layouts (cached by hash), translate Execute's neutral decisions into real GPU work.

**Dependency direction: `Sandbox → {backends} → Graph → Core`.** `Loom.Core` and `Loom.Graph` carry **no** graphics-API / windowing reference. **Introducing a graphics-API type into Core or Graph is the cardinal architectural violation** — it breaks backend portability. Barrier derivation is the hardest part of Execute; the reference is Hans-Kristian Arntzen's "Render graphs and Vulkan — a deep dive" (Granite).

---

## 3. The Pillars of Design (load-bearing — protect these)

- **Derived, not declared.** Users state reads/writes; ordering, barriers, layouts, aliasing, and queue sync are computed. *Any API that asks the user to specify order is a smell.* Instancing, indirect draws, and GPU culling must enter as describe-layer *intents*, never manual ordering knobs.
- **`.Writes` vs `.Blends`.** Produce-(may-clear) vs accumulate-onto-(load+blend). Barrier derivation needs the distinction.
- **Backend isolation / portability.** All procedural, struct-heavy, API-specific code stays in the backends. Core/Graph speak in abstract resources and intents so both backends stay swappable — and a third (Metal/WebGPU) could join. **Every new GPU feature must land in (or be `<Use…>`-gated for) both backends, with zero graphics-API types in Core/Graph.**
- **Pure per-frame closures.** The graph is built once and cached; each `.Draw`/`.Dispatch` body runs every frame. So a closure may capture **stable objects** — engine handles and app-side stable producers (a painter, a scene) — and read their **live** members at execute time; it must **not** capture a per-frame **value** (that snapshot freezes at build). Pull per-frame facts from `ctx` and per-frame data from neutral `Constants`/`Storage` buffers or from a stable producer passed as pass state. The `Draw<TState>`/`Dispatch<TState>` overloads let a body be written as a `static` lambda so accidental per-frame capture cannot compile.
- **No allocations on the hot path.** The steady per-frame render loop — pass closures, draw submission, the Execute schedule walk — allocates no managed memory, especially in Release. Per-frame data rides preallocated or pooled buffers and `ref struct`s (`DrawCall` is one); the graph is built once and cached, so frames stay GC-free.
- **You hand me draw lists.** Culling stays *optional and app-owned*. The engine may *offer* a frustum-cull helper and a `Scene` container (`Loom.Concepts`); it must not mandate a scene graph or seize draw submission.
- **The engine names no game concepts.** No `Camera`/`Sun`/`Scene` in Core/Graph; `FrameInputs` is just `DeltaSeconds`/`Time`. Per-frame view/world/light/time-of-day data rides neutral `Constants`/`Storage` buffers (`engine.Write(...)`); the app names its own views. This is what keeps Loom a *rendering* engine, not a game engine — and is load-bearing for reuse and portability.
- **Ping-pong via handle swap.** Two physical resources + a `(read, write)` tuple swapped in a normal loop; the graph follows the handles.
- **Plans are advisory.** `BarrierPlan` and `AliasPlan` are *inputs* a backend may use or ignore (an auto-synchronizing WebGPU backend ignores both); the derived per-attachment `AttachmentAction` load/store ops are what a tile-based/Metal-memoryless backend needs and are always available.
- **Fail loud, never silently degrade.** Every backend mapping switch (`Format`/`ResourceState`/`BlendMode`/`SamplerAddress`) ends in a `throw`; over-promised APIs (async-compute, multi-frame history ring) throw at derive time rather than silently doing the wrong thing.

---

## 4. Backend portability posture (Metal / WebGPU)

Backend isolation was independently verified clean — **no graphics-API type leaks into Core or Graph.** Adding a Metal or WebGPU backend is a backend-isolated, **additive** effort with **no irreversible dead-ends** in the current design. The seams that keep it open, all confirmed in code, must be preserved:

- **Opaque surface-creation seam** — `IRenderBackend.Create(object surface, …, EngineSettings)`. A Metal `CAMetalLayer` / WebGPU canvas slots in as just another `object` + settings subclass; no Core change.
- **Neutral matrix/NDC** — RH, +Y up, **z ∈ [0,1]** (exactly Metal/WebGPU's required depth range — no OpenGL `[-1,1]` assumption), with the clip fix-up isolated in a single neutral `ClipSpaceCorrection` matrix a WebGPU +Y-up backend overrides to identity.
- **Advisory `BarrierPlan`/`AliasPlan`** + **derived `AttachmentAction` load/store** — an auto-sync WebGPU backend ignores the barriers/aliasing and reads only the attachment actions; a tile-based Metal backend gets memoryless load/store for free.
- **Logical shader names** — no SPIR-V/DXIL hardcoded in Core/Graph; MSL/WGSL emit is a pure backend + build addition (Slang targets both).
- **Loud failure** — every mapping switch throws on an unmapped value, so a new backend fails fast on a missing arm rather than silently degrading. The ImGui pointer seam is confined to the opt-in app-side `Loom.UI.ImGui` bridge; the engine path is the pointer-free `DrawList` verb (wasm-safe).

**The one WebGPU-relevant friction** is the storage-state collapse (§5.1, optional): a WebGPU backend must treat every `Storage` access as read-write, foreclosing write-only storage textures until the state is split. Everything else is additive enum arms + new derivation, not a rewrite.

---

## 5. The remaining roadmap

### 5.1 Engine-capability gaps (the real engine work)

These need new describe-layer verbs / Execute derivation / backend machinery / a resource kind or format. Each must land on **both** backends as a derived intent. Ordered by how much they unlock.

| # | Gap | Missing primitive | What it unlocks |
|---|-----|-------------------|-----------------|
| ① | **GPU-driven draw + Hi-Z occlusion** — *the verb is built; the architecture is not* | `DrawCall.Indirect` + `.ReadsIndirect` exist on all three backends, so a compute pass can fill a draw's counts and the hand-off is derived (`AccessKind.ReadIndirect` → `ResourceState.IndirectArgument`). Still missing: a **count buffer**, multi-draw, a GPU culling pass, and Hi-Z (which also needs ②'s mip-chained depth target and the per-mip barrier state in [`subresource-state.md`](docs/design-notes/subresource-state.md)). | GPU-driven rendering at factory/foliage scale; GPU culling; occlusion. |
| ② | **Renderable cube / array / 3D / mipped / depth-array targets** | Graph targets are hardcoded single-layer single-mip 2D; `ResourceHandle`/`Format`/`Domain` carry no layer/dimension axis, and `BarrierPlan` tracks one state per whole resource — see [`subresource-state.md`](docs/design-notes/subresource-state.md) for the granularity design and the five decisions it turns on. (*Loading* cube/HDR textures already works — this is the **render-target** side.) | Rendering *into* a cubemap (point-light shadows, dynamic reflection probes, env capture); CSM as a clean layered array; froxel volumetrics (3D). |
| ③ | **Temporal stack** | `Persistent(size>1)` throws (single physical image); `FrameInputs` has no previous-frame view; no motion-vector channel. | TAA, temporal AA/denoise, temporal GI, any reprojection technique. The single biggest temporal blocker. |
| ④ | **MSAA resolve** — *built* | A multisampled domain's targets and pipelines take its sample count on all three backends, and a pass sampling a multisampled colour target reads its resolve, made where the graph takes the target from attachment to shader read (Vulkan `vkCmdResolveImage`, DirectX 12 `ResolveSubresource`, WebGPU a load-and-resolve pass); `TechniqueDescriptor.AlphaToCoverage`. The MSAA demo and `MsaaTests`. Still missing: a multisampled depth resolve (sampling an MSAA depth throws), an R32F or R32Uint one (no resolve on every backend: none resolves an integer, WebGPU no 32-bit float, so sampling either throws at derive) and reading back a multisampled target. | Forward MSAA anti-aliasing at cross-backend parity (alpha-to-coverage edges excepted: the sample mask is the driver's). |

**Optional / direction-dependent engine work** (enabling substrate, not gating any single feature):

- **Static instance buffers** — *built*: `IEngine.CreateInstances` uploads per-instance data once to GPU-resident memory (stored as a mesh is, so created, resolved and released the same way on all three backends) and `DrawCall.Static` draws a range of it, uploading nothing per frame. Still missing for a GPU-driven path: a compute pass writing into one (that is gap ①'s instance stream, `DrawCall.Gpu`).
- **Compute reads Constants and Storage** — *built*: a compute pass's `.Reads` binds as a draw's do, on all three backends: its `g.Constants` blocks to the technique's constant buffers, and its `g.Storage` buffers to the technique's read-only storage buffers (its read-write ones keep the dispatch's own), each throwing where the technique declares more than the pass reads. The Particles and Storage Scatter demos, `ComputeConstantsTests`, `StorageScatterTests`. So a compute pass can expand an app-written map every frame (scattered grass) with no per-frame upload.
- **Constants and Storage uploaded only when written** — *built*: `EngineBase` counts each buffer's writes and, per frame slot, the write its GPU copy holds, so a frame uploads only the slots behind (`ShadowSlot`, `ShadowSlotCount`; a backend that rebuilds those buffers calls `ForgetUploadedShadows`).
- **Async compute / multi-queue** — `Queue.AsyncCompute` exists but throws at derive; needs queue partitioning in `GraphSchedule` (the DAG is already preserved), cross-queue semaphores, and a compute queue per backend. Pure performance, never a correctness blocker.
- **Write-only / typed storage states + bindless** — `Storage` collapses to one `GENERAL` read-write state, and nothing indexes a descriptor array (a binding's `DescriptorCount` is its reflected slot span, so an arrayed binding is *described* correctly, but no shader declares one and no path selects an element). Splitting `Storage` into read/write/read-write is also the one **WebGPU**-relevant item; bindless is the enabler for a GPU-driven / virtual-texturing architecture.
- **Streaming / partial residency** — asset upload is synchronous and fully resident; virtual texturing / mega-texture streaming needs a residency seam.
- **Byte-aware transient alias packing** — `AliasPlan` partitions transients by lifetime interval with first-fit, which is optimal in bucket *count* but blind to byte sizes, and a bucket's block is sized to its largest member × frames-in-flight. A full-res `Rgba16F` sharing a bucket with a quarter-res mip therefore pays the full-res block twice over. Both VMA and D3D12 placed resources already support offset sub-allocation, so the fix is a packing change with no API surface. Immaterial at demo scale; real memory once a frame carries thousands of transients, so it belongs with the streaming / large-content direction.

### 5.2 Content / shader work (no new engine primitive)

Everything here rides existing verbs (`.Samples`/`.Draw`/`.Blends`/compute + `.ReadWrites`/ping-pong/`Constants` + `Storage`/`DrawList`). **Most of this category is already built** (see README) — the entries below are the ones that remain, and they are shader + buffer authoring, not engine work:

- **PBR + IBL sampling** — physically-based BRDF and image-based lighting over the cube/HDR source textures that already load. (Probe *capture/prefilter into* a cube is engine gap ②; a precomputed env map is pure content.)
- **Atmospheric scattering** — fullscreen passes + LUT precompute via compute, ping-pong where iterative.
- **Water + SSR (single-frame)** — a fullscreen ray-march `.Samples`-ing depth/colour + a surface shader.
- **Deformable terrain / GPU erosion sim** — compute over `Storage` ping-pong heightfields; geometry re-tessellated from a storage buffer.
- **Single-frame volumetric fog/clouds** — a ray-march fullscreen pass. (Froxel volumetrics hits gap ②; temporal accumulation hits gap ③.)

### 5.3 Hybrids (mostly shader + one small engine assist)

- **Cascaded shadow maps** — *already shipped* as N separate depth targets + N passes; the *clean* layered-array form would use gap ②.
- **Planetary / clipmap terrain + continuous LOD** — a CPU-driven clipmap is content-only; GPU-driven LOD selection wants gap ①.
- **Real-time GI** — probe-baked / single-bounce is content; any converged/accumulated technique wants gap ③.
- **Floating origin / camera-relative** — **no engine assist by design**: pure app-side matrix bookkeeping through `Constants`. Listed only because people expect an engine hook and there deliberately isn't one.

**Net:** roughly 70–80% of the conceivable "make a game look good" surface is shader/buffer/content authoring on existing verbs (much of it already built); ~20–30% is the four engine gaps in §5.1 — and those four happen to gate the highest-end targets (GPU-driven, reflection probes, TAA/temporal GI, froxel volumetrics).

---

## 6. Planned refactors & housekeeping

- **Stand up a dedicated backend tests project.** Backend integration tests (the kind recently removed when some backend-smoke demos were deleted from the sandbox) and the device-free CPU-testable slices of the backends (pipeline/descriptor hash-key equality, neutral-decision→barrier/PSO translation tables, placed-resource alias-offset math) belong in their own test project — run under NativeAOT — not as sandbox demos. This is the home for closing the "backends have zero unit tests" gap.
- **Small genuinely-open code items** (low-risk, tracked in [`CODE_REVIEW.md`](CODE_REVIEW.md)): `VertexLayout` offsets ignore attribute alignment; Grid Auto-then-Star measure ordering; the backends have zero CPU unit tests; no compute→compute / ping-pong barrier-derivation test; one SourceGen diagnostic uses a zero-length span.
- **Gamepads through SDL 3 beyond Windows.** The desktop host reads pads through SDL 3 (`SdlGamepadPump`), fetched from SDL's own Windows release builds by `Sdl3.targets`. SDL publishes no Linux builds and only a framework for macOS, so there the host still reads pads through GLFW: a natives package built from SDL's source release would close it. GLFW tells a pad's type only from its mapping's name.
- **Comment hygiene** (tracked in [`COMMENT_REVIEW.md`](COMMENT_REVIEW.md)): the in-source milestone/section/`PlanningUI.md §…` tags must be stripped to satisfy the self-contained-comment rule — a large but mechanical sweep, concentrated in `external/SUIsei` and the Vulkan backend.

---

## 7. Open decisions

Resolve each before the work it gates; record the decision on the board.

| # | Decision | Gates | Leaning |
|---|----------|-------|---------|
| 1 | Pursue the GPU-driven direction (count buffers + bindless + Hi-Z + a culling pass) at all? | §5.1 ①, optional bindless | **Still open, and deliberately so.** The indirect *verb* landed with the scale milestone because it is the derivation seam — a compute pass fills the arguments, a draw consumes them, ordering derived. That was cheap and additive; it does not commit to the architecture. Decide the rest by whether a target game needs tens-of-thousands-of-objects scale; it remains the largest single engine investment. |
| 2 | Cube/array **render-target** representation | §5.1 ② (probes, CSM arrays, froxels) | Add a layer/mip axis to `ResourceHandle` + a `Domain` array dimension; keep it a derived intent. |
| 3 | Temporal stack shape | §5.1 ③ (TAA/temporal GI) | A `Persistent(size:N)` ring of physical images + a previous-frame `Constants` channel + a velocity target; derive the ring ping-pong like JFA. |
| 4 | Split `Storage` into read/write/read-write states | WebGPU backend; typed storage images | Additive, no API break — do it when a WebGPU backend or a write-only-storage shader needs it. |
| 5 | Terrain representation, *if* the large-world direction is taken | clipmap heightfield vs voxel/SDF | Driven by whether caves/overhangs/digging are required. |

---

## 8. Verification

Every new feature is proven the way the existing ones were:

1. A focused `Loom.Sandbox` demo.
2. A deterministic offscreen render in `Loom.Tests` — the sandbox is interactive-only, so verification runs in-process (`GalleryCapture`) rather than through a capture flag.
3. **Cross-backend pixel diff** — Vulkan vs DirectX 12 within tolerance (4/255 per channel, <0.5% of pixels), via `Scripts/Verification/diff.ps1`; the automated `GalleryParityTests` asserts it (GPU-gated).
4. Golden-image regression (`GalleryGoldenTests` against the committed baselines under `Loom.Tests/Goldens/`; `LOOM_GOLDEN_UPDATE` regenerates them) guards against drift.
5. Validation layers / DX12 info-queue stay clean.

The debug spine (channel viewer, GPU timing) exists to make this loop fast for the heavier §5.1 features.

---

## 9. The UI track (SUIsei)

Game/HUD UI is a **separate, standalone subsystem**: `SUIsei`, a GPU-free WinUI-3-compatible XAML UI runtime (compile-time `{x:Bind}`, NativeAOT-clean), vendored as the `external/SUIsei` submodule with its own repo and **its own design bible at [`external/SUIsei/Planning.md`](external/SUIsei/Planning.md)**. It reaches the GPU only through the neutral, UI-agnostic `IRenderContext.DrawList` verb (the same path ImGui rides) via the thin `Loom.UI.SUIsei` glue — so **no UI library is referenced by either backend, and no UI-specific type enters Core/Graph.** See that document for the UI roadmap and status; this file does not duplicate it.

---

## 10. The audio track

Audio is a **separate subsystem beside the render graph**: `Loom.Audio`, a mixer that depends only on the BCL (as `Loom.Input` does), with each host opening its platform's output behind the `IAudioDevice` seam (SDL 3 on the desktop, AAudio on Android, an AudioWorklet in the browser). It shares nothing with the graph: no graphics-API type and no audio API type enters `Loom.Audio`, and no audio type enters Graph. [`docs/audio.md`](docs/audio.md) documents what is built; epic #10 tracks the work.

Its own load-bearing properties, beside §3's:

- **Lowest latency the platform offers on native hosts**, because the target is software synthesizers and procedural music played live. The mix runs on the device's own audio thread at the device's own rate. The browser trades latency for a lead the frame thread renders ahead, since the .NET runtime can't run on the browser's audio thread.
- **The game and audio threads share no lock**, and neither allocates once running: commands and events cross on single-producer, single-consumer rings, continuous controls through `AudioParameter`.
- **Fail loud, except for a missing speaker.** Misuse throws on the frame thread (a wrong-thread call, a full command queue, a clip at the wrong rate, a source played twice), and an exception in a source is thrown again from `Update`. A machine with no output is not an app bug, so it runs silent on a `NullAudioDevice` with a logged warning.

### Remaining roadmap

| Item | Notes |
|---|---|
| **More decoders** | FLAC next (lossless, simple), Opus if wanted, each written in C# in `Loom.Audio` as the Vorbis decoder is: no dependency, allocation-free while streaming, and the same code on every host, the browser included. |
| **3D beyond stereo panning** | 3D voices pan, attenuate and Doppler-shift for a listener the app feeds (positions, never a camera). Not built: HRTF for above and behind, occlusion, reverb zones, sound cones. |
| **Low-latency WASAPI on Windows** | SDL 3 stops at shared mode's 10 ms period. An exclusive-mode (or `IAudioClient3` small-period) device would reach 2–3 ms. |
| **Browser: a shared ring instead of a message per frame** | The frame thread posts each frame's samples to the worklet as a new `Float32Array`. The runtime is already multithreaded (cross-origin isolated), so a `SharedArrayBuffer` ring would remove that per-frame garbage and the message hop. |
| **Collections on the audio thread** | A full blocking collection the game sets off pauses the audio thread with every other. Decide whether the engine sets `GCSettings.LatencyMode` to `SustainedLowLatency` while audio runs, or leaves it to the app (it changes the whole process's collector). |
