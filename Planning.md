# Loom — Project Planning & Design Bible

> **What this is.** Loom is a declarative, render-graph-driven rendering engine for .NET, built on Vulkan via Silk.NET. You describe *what* each rendering pass reads and writes; Loom derives *when* things run, every barrier and image-layout transition, transient memory aliasing, and CPU↔GPU synchronization. The procedural, struct-heavy nature of Vulkan is quarantined in one backend layer and never leaks into the API you author against.
>
> **What this document is for.** This is the source-of-truth handover from the design session that produced Loom's concept. It captures the architecture, the API surface, the effect cookbook, the decisions (and *why* we made them), the open questions, and the build roadmap. It is written to be read both by a human and by a fresh Claude Code session.
>
> **First Claude Code prompt:** *"Read Planning.md top to bottom, then let's start on Milestone 0 — stand up the solution and get a Vulkan-cleared window running."*

---

## 1. The core idea

The dream: a .NET engine that lets you *fluently and declaratively* compose a reusable rendering pipeline, where synchronization, timing, uploads, and resource lifetimes are handled for you.

The hurdle that historically blocks this: Vulkan wants to be called procedurally, with massive blocks of structs everywhere, which fights any attempt to build a clean object tree.

**The reframe that dissolves the hurdle:** the procedural, struct-heavy nature of Vulkan is a property of the *backend*, not the *frontend*. It does not have to leak upward at all. The load-bearing fact is that `vkCreate*` calls **copy** the data they need. Once `vkCreateGraphicsPipelines` returns, you are free to throw away the `CreateInfo`, every array it pointed at, and the whole `pNext` chain — Vulkan has internalized everything it cares about. (Shader modules are the same: destroy them the instant pipeline creation returns.)

That means the managed object tree can be exactly the clean, fluent, GC'd thing we want. The struct soup only needs to *transiently exist* — pinned in native memory for the duration of a single create call, then gone. The marshaling step is the **seam**: the declarative tree on one side, a flatten-to-native-arena step at call time on the other. All the procedural ugliness lives inside that seam, where nobody authoring a frame has to look at it.

**The one principle everything else follows from:** you never describe *when* or *in what order*. You only describe *what each pass reads and writes*. Ordering, barriers, layout transitions, memory aliasing, and queue synchronization are all *derived* from the dependency edges. Shadows, compute, cross-frame history, GPU readback, and async queues all enter the system as new *resource kinds* or new *edge directions* — and the schedule falls out every time.

This is why the API stays intuitive no matter how much we add: the vocabulary never grows past "declare a resource, declare a read/write intent."

The metaphor the project is named for: a loom is not told the order to lay each thread. You warp it, declare the pattern, and the woven cloth falls out of the structure. Passes are threads; a frame is the cloth.

---

## 2. Architecture: Describe → Bake → Execute

Loom is three layers. Keeping them separate is what makes the whole thing tractable.

### Describe
Immutable, hashable, managed objects. This is where the fluent builders live and where allocation is allowed to be ergonomic.

- A **technique** (think "material template") describes shader stages, blend / depth / raster state, and vertex or instance layout.
- A **render graph** describes passes and, for each pass, the resources it reads and writes.

This layer allocates freely and is the only layer the user authors against directly.

### Bake
Takes a description, marshals it transiently to native structs, produces concrete Vulkan objects (`VkPipeline`, descriptor set layouts, etc.), and **caches them by hash**. This is where the procedural Vulkan calls happen — once each, behind a cache. The marshaling seam (Section 1) is this layer's hot path.

### Execute
Per frame: topologically sort the passes, **derive** the barriers and layout transitions from the declared read/write intents, alias transient memory, and record command buffers. This layer is imperative and tight — no fluent allocations in the hot loop — but it is *generated*. The user never hand-writes a barrier.

### Why barriers can't be declared directly (and what we do instead)
You cannot make barriers declarative *directly*, because the correct barrier for resource R at pass N depends on what happened to R at N−1 and what happens at N+1. What you *can* do is let the user declare **usage intent** ("this pass samples texture A and writes color to B") and have the graph compute the barriers from the global picture. Barrier derivation is the single hardest part of the Execute layer, and it is where naive attempts quietly produce either corruption or pipeline stalls. Read the prior art (Section 4) before implementing it.

---

## 3. Platform & dependencies

### Vulkan 1.3 as the baseline (strong opinion)
A huge amount of what historically made this dream feel impossible was 1.0-era boilerplate. Targeting 1.3 makes the following **core** (no extension juggling):

- **Dynamic rendering** (`VK_KHR_dynamic_rendering`, core in 1.3). This kills `VkRenderPass` / `VkFramebuffer` objects *entirely* — an enormous category of struct soup and an entire baking step gone. The render graph no longer has to compile renderpass/subpass objects. This is the single biggest simplification.
- **Synchronization2** (`VK_KHR_synchronization2`, core in 1.3). Makes barriers far cleaner to express; the Execute layer emits `vkCmdPipelineBarrier2`.
- **Timeline semaphores** (core in 1.2). Collapse most of the CPU↔GPU and cross-queue sync story into a single monotonic counter.

If your mental model of "Vulkan is unbearably procedural" was formed around 2016-era tutorials with explicit subpasses and binary-semaphore juggling, half of that pain is simply obsolete on this baseline. (Vulkan 1.4 is also fine to target — all three features above remain core — but 1.3 is the widely-supported floor and what the design assumes.)

### Descriptors via SPIR-V reflection
Reflect the SPIR-V to auto-derive descriptor set layouts and push-constant ranges. The user declares *shaders*; Loom figures out the binding layout. This removes another large chunk of struct-filling from the frontend. **Decided (M7): use Slang's own reflection** — Slang is already the shader compiler, so its reflection comes for free with no extra dependency (`spirv-reflect` / SPIRV-Cross were the alternatives). This resolves the §10 open question; it lands in Milestone 7 (the `DefineTechnique` builder).

### HDR display output (baseline capability)
The engine renders internally in HDR (`Rgba16F` throughout lighting/bloom/tonemap), but **true HDR *output*** — presenting to an HDR display rather than tonemapping down to 8-bit sRGB — is a first-class capability, not an afterthought. The backbuffer format and swapchain colour space are **negotiated against display capability** (`VK_COLOR_SPACE_HDR10_ST2084_EXT` / `…_EXTENDED_SRGB_LINEAR_EXT` scRGB), not hardcoded; the HDR-capable formats (`Rgb10A2`, `Rgba16F`) already exist in the format vocabulary. An `EngineSettings.HdrOutput` toggle selects HDR vs the SDR fallback, and the terminal/tonemap pass applies the matching output transform (PQ/scRGB vs sRGB). Lands in Milestone 7.

### Stack
- **.NET 10** (`net10.0`).
- **Silk.NET** for Vulkan, windowing, input, and math: `Silk.NET.Vulkan`, `Silk.NET.Windowing`, `Silk.NET.Input`, `Silk.NET.Maths`. Dynamic rendering / sync2 are enabled as device features (`VkPhysicalDeviceVulkan13Features`), not loaded as extensions.
- **Shader compilation**: **Slang**, decided and in place (M0/M1) — compiled to SPIR-V **offline** via the bundled `slangc` (the `Slang.Sdk` NuGet, hermetic, win-x64/win-arm64). **Reflection also uses Slang** (decided M7), not `spirv-reflect`. GLSL remains a possible fallback; runtime compilation (`shaderc`) is unused for now.
- **Memory**: candidate is the Vulkan Memory Allocator (VMA) for sub-allocation and, importantly, its **aliasing** support, which the transient-resource system needs. Decision deferred — see Section 9.

---

## 4. Prior art to read before building

- **Yuriy O'Donnell — "FrameGraph: Extensible Rendering Architecture in Frostbite" (GDC 2017).** The origin of the render-graph pattern as used in modern AAA engines. Read for the describe/execute split and resource lifetime model.
- **Hans-Kristian Arntzen (Themaister) — "Render graphs and Vulkan — a deep dive" (Granite).** The single best thing to read before implementing barrier derivation. His Granite engine is the reference implementation of the exact thing Loom is. Barrier derivation is the crux; this is where to learn it.
- **Jorge Jimenez — "Next Generation Post Processing in Call of Duty: Advanced Warfare" (SIGGRAPH 2014).** The bloom downsample/upsample filters (13-tap Karis-averaged downsample, 3×3 tent upsample) we use in the bloom sub-graph.
- **Rong & Tan — "Jump Flooding in GPU with Applications to Voronoi Diagram and Distance Transform" (2006).** The jump flood algorithm behind the world-space SDF outline.
- **Veldrid (mellinoe)** and **Stride engine's rendering layer** — two .NET prior arts. Neither is render-graph-driven the way Loom is, but both are worth reading for how they shaped a *describe* layer in C#.

---

## 5. The API surface (canonical vocabulary)

This is the fluent surface to author against. Treat it as the spec; keep it stable across the codebase.

> **Status (M7).** This vocabulary is the *target*; **Milestone 7 brings the code in line with it.** Where today's code differs, it's flagged inline as *(current: …)*. The biggest gaps M7 closes: there was no neutral `Engine` facade (the host built graphs via `RenderGraph.Define` and drove a backend `GraphExecutor` directly), techniques were string-keyed backend records rather than a `DefineTechnique` builder, per-frame inputs were mutable executor properties rather than a `FrameInputs` value, and `ResourceHandle` was one untyped class rather than `ColorHandle`/`DepthHandle`/`BufferHandle`.

### Entry points
- `engine.DefineGraph(g => { ... })` — builds, bakes, and caches a graph. Returns a `Graph`. The engine is obtained from the backend registry: `Engine.AvailableEngines.FirstOrDefault(e => e.Name == settings.PreferredBackend) ?? Engine.AvailableEngines.First()`, then `engine = backend.Create(surface, settings)` — app code names no backend type. *(current: `RenderGraph.Define(...)`, a static factory in `Loom.Core`, plus a `Loom.Vulkan.GraphExecutor` constructed directly.)*
- `engine.DefineTechnique("name", t => { ... })` — builds and caches a technique. Returns a `Technique` handle. *(current: a `Dictionary<string, TechniqueTemplate>` where `TechniqueTemplate` is a hand-counted `Loom.Vulkan` record — M7 moves this to a Core builder with Slang reflection.)*
- `graph.Render(frameInputs)` — the entire per-frame hot-path call. Returns a frame result (carries readback handles). *(current: mutable props on `GraphExecutor` — `.Camera`/`.Time`/`.SunView`/`.HoverId` — plus `DrawFrame(delta)`.)*

### Resources & domains
- `g.Domain("name", width, height)` — a **fixed-size resolution domain**. Everything in a domain renders at that resolution. The mechanism behind pixelation (Section 10); the right tool for pixel-art (a fixed 320×180 world stays pixel-consistent at any window size).
- `g.Domain("name", scale: f)` *(M7)* — a **resize-tracking domain** sized to a fraction of the backbuffer; re-resolves on resize. `g.Screen` *(M7)* is the native-resolution screen domain.
- `g.Backbuffer` — the swapchain colour target (native resolution; backend-negotiated format/colour space, incl. HDR). `g.Backbuffer.Domain` is the native-resolution domain for sibling targets.
- `domain.Color("name", Format.X)` → a `ColorHandle`; `domain.Color("name", Format.X, scale: f)` → fractional (mip chains); `domain.Depth("name", Format.X)` → a `DepthHandle`. *(M7: typed handles; `g.Buffer` → `BufferHandle`.)*
- `g.Persistent("name", Format.X, size: N)` — a resource whose lifetime is **forever**; the aliaser never recycles its memory. Cross-frame history (e.g. auto-exposure).
- A handle exposes `.Domain` (so helpers can inherit their parent's domain). *(current: one untyped `ResourceHandle` with a `Kind` enum — M7 splits it into subtypes so verb misuse is a compile error.)*

### Pass builders
Raster pass: `g.Pass("name")` then chain intents:
- `.Writes(resource, ...)` — color output(s). The pass *produces* this; the engine may clear it.
- `.Blends(resource)` — read-modify-write: `LoadOp.Load` + the technique's blend does the accumulate. The pass is simultaneously the last reader and next writer of this resource.
- `.Samples(resource, ...)` — sampled (shader-read) inputs.
- `.WritesDepth(depth)` — depth write.
- `.ReadsDepth(depth)` — depth test, no write.
- `.ReadWrites(resource)` — general read-modify-write (storage images/buffers; used by compute).
- `.Queue(Queue.AsyncCompute)` — assign the pass to a queue; the graph derives the cross-queue timeline sync.
- `.Draw(ctx => { ... })` — the record callback. **Must be pure w.r.t. graph structure** (see Section 8).

Compute pass: `g.Compute("name")` then intents, then `.Dispatch(ctx => ctx.Dispatch(Technique.X, groups: (x,y,z), ...))`.

Terminal: `g.Present(resource)`.

GPU→CPU: `var handle = g.Readback(resource);` then on the frame result, `await result.Sample(handle, x, y)`.

### Inside `.Draw` / `.Dispatch` — the execution context (`ctx`)
- `ctx.Draw(drawList, Technique.X)` and `ctx.Draw(drawList, Technique.X, view: cameraOrLightView)` — record draws with a technique, optionally from an arbitrary view. *(current: `ctx.Draw(Mesh, "name")` — a single captured mesh, no draw lists; a `Scene`/draw-list abstraction is future work.)*
- `ctx.FullScreen(Technique.X, ...args)` — a fullscreen pass (paired with `.FullScreenTriangle()` techniques). Includes the blit/upscale: a nearest sample of one input **is** the pixelation step. *(There is no separate `ctx.Blit`/`Filter` — it folded into `FullScreen` + the technique's filter.)*
- `ctx.Dispatch(Technique.X, groups, ...)` — dispatch a compute technique.
- `ctx.Camera`, `ctx.DeltaTime`, `ctx.Time`, `ctx.SunView`, `ctx.HoverId` — per-frame inputs, **pulled** from the context, not captured (keeps the graph frame-independent and cacheable). *(`ctx.Scene` and a structured `ctx.Hover` (`.ObjectId`/`.Color`) are the target; current code exposes `ctx.HoverId` as a bare `uint`.)*

### Technique builders
- `.Vertex("path.spv")`, `.Fragment("path.spv")`.
- `.FullScreenTriangle()` — no vertex buffer; uses the `gl_VertexIndex` trick.
- `.VertexLayout<T>()` / `.Instance<T>()` — vertex / per-instance data layout.
- `.Depth(test:, write:, compare: Compare.X)` — e.g. `Compare.GreaterEqual` for reverse-Z.
- `.Blend(Blend.X)` — `Blend.AlphaOver`, `Blend.Additive`.
- `.Cull(Cull.X)` — `Cull.Back`, `Cull.None`.
- Descriptor set layout and push-constant ranges are **reflected from the shader**, not declared here. *(M7: via **Slang reflection** — the §10 decision. The technique names its shader **logically**; the backend resolves the bytecode (SPIR-V today, DXIL for a future DX12 backend), so the describe layer never names a `.spv` path. Until M7 this is a hand-counted `TechniqueTemplate` in `Loom.Vulkan`.)*

### Frame inputs
`graph.Render(new FrameInputs { Camera = camera, Scene = scene })` — plus hover/cursor info for picking and outlines. `Scene` is app-owned and exposes draw lists: `Scene.Opaque`, `Scene.Transparent.SortedBackToFront(camera)`, `Scene.Lights`, `Scene.Sun` (`.View`, `.Cascade(i)`), `Scene.Hud` (`.Items`), `Scene.Grading`. *(M7 introduces the `FrameInputs` value + `Render`; a full `Scene`/draw-list abstraction is later work — current code sets mutable executor props and captures meshes into pass closures.)*

### Formats used so far
`Rgba8Srgb`, `Rgba8Unorm`, `Rgb10A2`, `Rgba16F`, `R16F`, `R32Uint`, `Rg16F`, `Depth32F`.

---

## 6. The reference frame

The canonical demo and the proof that the whole vocabulary works: **deferred shading + forward transparency + bloom + pixelation + a screen-space HUD.** The frame *is* a render graph; you author it once and feed it draw lists each frame.

The one real design decision in this frame is the pixelation. The authentic pixel-game look is *not* a post-process filter on a full-res image — it is to render the entire 3D world into a small target (e.g. 320×180) so geometry, lighting, and transparency all happen at that resolution, then do a single **nearest-neighbor upscale** to the screen. That upscale *is* the pixelation. Bloom therefore lives in the low-res domain (chunky, pixel-consistent). The HUD is taken literally as screen-space: native resolution, on top, untouched by bloom or pixelation.

```csharp
var graph = engine.DefineGraph(g =>
{
    // Two resolution domains. The whole 3D world lives in the small one;
    // crossing to screen is an explicit nearest-neighbor upscale — that IS the pixelation.
    var world  = g.Domain("world", width: 320, height: 180);
    var screen = g.Domain.Backbuffer;            // native swapchain resolution

    // --- G-buffer, sized to the world domain ---
    var albedo   = world.Color("albedo",   Format.Rgba8Srgb);
    var normal   = world.Color("normal",   Format.Rgb10A2);
    var material = world.Color("material", Format.Rgba8Unorm);  // roughness, metal, ao
    var depth    = world.Depth("depth",    Format.Depth32F);
    var lit      = world.Color("lit",      Format.Rgba16F);     // HDR, so bloom has range

    // ---------------- Deferred geometry ----------------
    g.Pass("gbuffer")
     .Writes(albedo, normal, material)
     .WritesDepth(depth)
     .Draw(ctx => ctx.Draw(ctx.Scene.Opaque, Technique.GBuffer));

    // ---------------- Lighting (consumes the G-buffer) ----------------
    g.Pass("lighting")
     .Samples(albedo, normal, material, depth)
     .Writes(lit)
     .Draw(ctx => ctx.FullScreen(Technique.DeferredLighting, ctx.Scene.Lights, ctx.Camera));

    // ---------------- Forward transparency, over the lit result ----------------
    g.Pass("transparent")
     .Writes(lit)              // blend over existing lit colour
     .ReadsDepth(depth)        // test against opaque depth, don't write it
     .Draw(ctx => ctx.Draw(ctx.Scene.Transparent.SortedBackToFront(ctx.Camera),
                           Technique.Forward));

    // ---------------- Bloom, still in the world domain (stays chunky) ----------------
    var glow = g.Bloom(lit, threshold: 1.0f, mips: 5);   // expands into a sub-graph (Section 7)

    // ---------------- Cross domains: nearest-neighbour upscale to screen ----------------
    g.Pass("upscale")
     .Samples(glow)
     .Writes(screen.Color)
     .Draw(ctx => ctx.Blit(glow, Filter.Nearest));       // <- the pixel look

    // ---------------- Screen-space HUD: native res, on top, no bloom ----------------
    g.Pass("hud")
     .Writes(screen.Color)     // ordered after upscale (write-after-write, program order)
     .Draw(ctx => ctx.Draw(ctx.Scene.Hud, Technique.Ui));

    g.Present(screen.Color);
});

// Every frame — this is the whole hot-path call site:
graph.Render(new FrameInputs { Camera = camera, Scene = scene });
```

Two contrasting techniques — opaque G-buffer write vs alpha-blended forward:

```csharp
var gbuffer = engine.DefineTechnique("gbuffer", t => t
    .Vertex("shaders/gbuffer.vert.spv")
    .Fragment("shaders/gbuffer.frag.spv")
    .VertexLayout<Vertex>()
    .Depth(test: true, write: true, compare: Compare.GreaterEqual)   // reverse-Z
    .Cull(Cull.Back));

var forward = engine.DefineTechnique("forward", t => t
    .Vertex("shaders/forward.vert.spv")
    .Fragment("shaders/forward.frag.spv")
    .VertexLayout<Vertex>()
    .Depth(test: true, write: false, compare: Compare.GreaterEqual)  // test, don't write
    .Blend(Blend.AlphaOver)
    .Cull(Cull.None));
```

### What the engine derived (and you never wrote)
- **Pass ordering** — from the read/write edges. `lighting` waits on `gbuffer` (samples its outputs); `transparent` waits on `lighting` (RAW on `lit`) and on `gbuffer` (depth); `bloom` waits on `transparent`; the two backbuffer writers (`upscale`, `hud`) are ordered by program order because they touch the same resource with no read between them.
- **Every barrier and image-layout transition** — the G-buffer targets flip `COLOR_ATTACHMENT_OPTIMAL → SHADER_READ_ONLY` between `gbuffer` and `lighting`; depth goes `DEPTH_ATTACHMENT → DEPTH_READ_ONLY` for the transparent pass; bloom mips ping-pong through their own transitions. You declared intent; the graph emits the `vkCmdPipelineBarrier2` calls.
- **Transient memory aliasing** — `albedo`, `normal`, `material` are dead after `lighting`, so their memory is reused for the bloom mip chain.
- **Swapchain sync and frames-in-flight** — acquire semaphore, present, timeline/fence juggling for double/triple buffering — all behind `g.Present(...)`.
- **The struct soup** — every `CreateInfo`, every `pNext` chain, the reflected descriptor layouts — flattened transiently into pinned native memory at bake time, behind the cache, and gone.

### Flexibility payoff
If the HUD should share the pixel grid, move its pass from `screen` to `world` and let it ride the same upscale — a one-line change, no resync, because the graph recomputes the whole barrier/ordering picture from the new edges.

### Caveats baked into this frame
- `g.Bloom(...)` is a reusable sub-graph fragment (Section 7), not an engine primitive.
- Reverse-Z (`GreaterEqual` + a flipped projection) is an opinion baked in for depth precision. Drop it if undesired — it's a per-technique setting.

---

## 7. Effect cookbook

Every effect below is the same two moves — declare resources, declare read/write intent — wearing a different hat. Each entry notes the *novel primitive or edge direction* it introduces.

### 7.1 Bloom (a reusable sub-graph)
`g.Bloom(...)` is an **extension method on the graph builder** that adds passes and returns a handle. Nothing about it is privileged. It is the canonical example of a sub-graph composing into a parent.

The algorithm: bright-pass/prefilter → N downsamples (13-tap Karis-averaged) → N additive upsamples (3×3 tent) → composite. The Karis average and the specific filters are what stop bloom from flickering/pulsating as the camera moves.

```csharp
public static ResourceHandle Bloom(
    this GraphBuilder g, ResourceHandle source, float threshold, int mips, float intensity = 0.04f)
{
    var domain = source.Domain;              // inherit the world domain (320×180)
    var chain  = new ResourceHandle[mips];
    for (int i = 0; i < mips; i++)
        chain[i] = domain.Color($"bloom_mip{i}", Format.Rgba16F, scale: 1f / (1 << (i + 1)));

    // Bright-pass + Karis average, halving into mip0. Threshold lives in a push constant.
    g.Pass("bloom.prefilter")
     .Samples(source)
     .Writes(chain[0])
     .Draw(ctx => ctx.FullScreen(Technique.BloomPrefilter, threshold));

    // Downsample: each mip is the 13-tap filtered half of the one above.
    for (int i = 1; i < mips; i++)
        g.Pass($"bloom.down{i}")
         .Samples(chain[i - 1])
         .Writes(chain[i])
         .Draw(ctx => ctx.FullScreen(Technique.BloomDownsample));

    // Upsample: from the smallest mip back up, 3×3 tent, ADDITIVELY accumulated
    // into the larger mip the downsample already wrote.
    for (int i = mips - 2; i >= 0; i--)
        g.Pass($"bloom.up{i}")
         .Samples(chain[i + 1])    // the smaller, already-accumulated mip
         .Blends(chain[i])         // read-modify-write: LoadOp.Load + additive blend
         .Draw(ctx => ctx.FullScreen(Technique.BloomUpsample));

    // Composite the fully-accumulated mip0 back onto the original lit image.
    var output = domain.Color("lit_bloomed", Format.Rgba16F);
    g.Pass("bloom.composite")
     .Samples(source, chain[0])
     .Writes(output)
     .Draw(ctx => ctx.FullScreen(Technique.BloomComposite, intensity));

    return output;
}
```

The additive upsample technique (additive-ness is *technique* state, not pass state):

```csharp
var bloomUp = engine.DefineTechnique("bloom.up", t => t
    .FullScreenTriangle()
    .Fragment("shaders/bloom_upsample.frag.spv")
    .Blend(Blend.Additive)
    .Depth(test: false, write: false));
```

**What the graph derives here:** the upsample passes **cannot run in parallel** — each reads the smaller mip that the previous upsample just accumulated into, so the RAW edges force strict `mip4 → mip3 → … → mip0` ordering, and the same machinery emits a barrier between each level. You wrote a `for` loop; the serialization and barriers are derived. Also, the bloom mips alias the freed G-buffer memory (their lifetimes don't overlap the G-buffer's).

The `.Writes` vs `.Blends` distinction is load-bearing precisely because of this pass. A graph that only knew `.Writes` would treat the additive upsample as a plain writer, miss its dependency on the downsample's prior contents, and either clear them (losing the accumulation) or skip the barrier (corruption). `.Blends` is the one shape that says "I am simultaneously the last reader and next writer of this resource."

### 7.2 Shadows — multiple views of the scene
**Novel primitive:** the `view:` parameter. Drawing "the scene" and "the scene from the sun" is the same pass with a different view.

```csharp
var shadow    = g.Domain("sun_shadow", 2048, 2048);
var shadowMap = shadow.Depth("sun_depth", Format.Depth32F);

g.Pass("shadow.sun")
 .WritesDepth(shadowMap)
 .Draw(ctx => ctx.Draw(ctx.Scene.Opaque, Technique.DepthOnly, view: ctx.Scene.Sun.View));

// lighting gains ONE sampled input and one extra argument — nothing else changes:
g.Pass("lighting")
 .Samples(albedo, normal, material, depth, shadowMap)
 .Writes(lit)
 .Draw(ctx => ctx.FullScreen(Technique.DeferredLighting,
                             ctx.Scene.Lights, ctx.Camera, ctx.Scene.Sun));
```

Cascades are the bloom loop again — N depth targets, one pass each, `ctx.Scene.Sun.Cascade(i)`.

### 7.3 Auto-exposure — cross-frame state + compute
**Two novel primitives:** `g.Persistent` (a resource that survives between frames; the aliaser leaves it alone) and `g.Compute` (the barrier machinery is not raster-only — it emits the compute→graphics barrier).

```csharp
var exposure = g.Persistent("exposure", Format.R16F, size: 1);   // survives frame to frame

g.Compute("autoexposure")
 .Samples(lit)
 .ReadWrites(exposure)        // read prior value, write adapted value
 .Dispatch(ctx => ctx.Run(Technique.AutoExposure, groups: (1,1,1), dt: ctx.DeltaTime));

g.Pass("tonemap")
 .Samples(glow, exposure)     // HDR → display range, before the upscale
 .Writes(world.Color("tonemapped", Format.Rgba8Unorm))
 .Draw(ctx => ctx.FullScreen(Technique.Tonemap, ctx.Scene.Grading));
```

This is the *same* mechanism TAA uses (a history buffer) — but see Section 10 for why TAA itself is off the table for this aesthetic and temporal exposure is fine.

### 7.4 Picking — GPU→CPU readback
**Novel edge direction:** `g.Readback` hands results *back* to the CPU. The latency is in the API (it returns a `Task`, resolving ~1–2 frames later) because readback is genuinely a frame or two old, and pretending otherwise would hide a pipeline stall.

```csharp
var ids = world.Color("object_id", Format.R32Uint);

g.Pass("id")
 .Writes(ids)
 .WritesDepth(depth)          // reuse opaque depth so occlusion is correct
 .Draw(ctx => ctx.Draw(ctx.Scene.Opaque, Technique.WriteId));

var pick = g.Readback(ids);   // declare you want this on the CPU
```

```csharp
var result   = graph.Render(frame);
uint hovered = await result.Sample(pick, mouseX, mouseY);  // resolves ~1-2 frames later
```

### 7.5 SDF hover borders — two completely different mechanisms
The request "SDF hover borders on items" splits along the engine's seam: a border on a **HUD item** is a *material* question (no graph change); a border on a **world object** is a *graph* question (a new sub-graph).

**HUD items — analytic SDF, zero graph changes.** A rounded-rect distance field is closed-form: no texture, no pass. It lives entirely in the UI technique's fragment shader, and "hover" is per-instance data.

```glsl
// per-item instance data: halfSize, cornerRadius, hover (0..1)
float d      = sdRoundedBox(localPos, halfSize, cornerRadius);   // <0 inside
float fill   = smoothstep( 0.5, -0.5, d);
float border = smoothstep( 1.5,  0.0, abs(d)) * (0.4 + hover);   // ring thickens on hover
float glow   = exp(-max(d,0.0) * 0.12) * hover;                  // outward falloff
outColor = vec4(mix(fillCol, edgeCol, border), fill + border + glow);
```

```csharp
var ui = engine.DefineTechnique("ui", t => t
    .Vertex("shaders/ui.vert.spv")
    .Fragment("shaders/ui_sdf.frag.spv")
    .Instance<UiItem>()              // halfSize, radius, colours, hover
    .Blend(Blend.AlphaOver)
    .Depth(test: false, write: false));

// the HUD pass is UNCHANGED:
g.Pass("hud").Writes(screen.Color).Draw(ctx => ctx.Draw(ctx.Scene.Hud, Technique.Ui));
```

The hover factor is animated CPU-side from a cursor test (`item.Hover += contains ? +dt*8 : -dt*8`). Not everything needs the graph, and the engine should let the cheap effects stay cheap.

**World objects — generated SDF via jump flood (JFA).** The silhouette is arbitrary, so you *grow* the distance field on the GPU. The `object_id` buffer from picking (7.4) is the seed source — same buffer, new consumer.

```csharp
// Run it in the low-res world domain so the outline rides the pixel grid.
var seedA = world.Color("outline_a", Format.Rg16F);   // each pixel stores nearest-seed coords
var seedB = world.Color("outline_b", Format.Rg16F);

// Seed: pixels matching the hovered id store their own coords; everything else a sentinel.
g.Pass("outline.seed")
 .Samples(ids)                                    // the object_id buffer
 .Writes(seedA)
 .Draw(ctx => ctx.FullScreen(Technique.JfaSeed, ctx.Hover.ObjectId));

// Jump flood: ping-pong, halving the step. Outline is thin, so only flood as far as needed.
var (read, write) = (seedA, seedB);
for (int step = OutlineReach; step >= 1; step >>= 1)     // e.g. 4 -> 2 -> 1
{
    g.Pass($"outline.jfa{step}")
     .Samples(read)
     .Writes(write)
     .Draw(ctx => ctx.FullScreen(Technique.JfaStep, step));
    (read, write) = (write, read);                       // swap handles; the graph follows
}

// Resolve nearest-seed distance into a ring, composited over lit.
var outlined = world.Color("lit_outlined", Format.Rgba16F);
g.Pass("outline.resolve")
 .Samples(read, lit)             // `read` holds the final flood after the swaps
 .Writes(outlined)
 .Draw(ctx => ctx.FullScreen(Technique.OutlineRing, ctx.Hover.Colour, width: 2f, glow: 1.5f));
```

**Why halving jumps work:** any offset up to the image width is the sum of halving jumps (it's binary), so a seed coordinate reaches every pixel in log₂(n) passes instead of n. After the flood, each pixel holds its nearest seed's coordinate; the distance is one subtraction, and the ring is a threshold on that distance.

**What the graph derives:** only **two** physical `Rg16F` buffers exist regardless of pass count (the tuple swap aliases them); the RAW edges through the alternating handles force strict serialization and a barrier between each level; and because both buffers stay live across the whole bounce, the aliaser won't recycle them mid-flood.

**Practical notes:** pure JFA can mis-flood a rare pixel — one extra pass ("JFA+1") fixes it, irrelevant for a 2px outline. A full distance field wants log₂(320) ≈ 9 passes, but an outline only needs distance out to its own width, so `OutlineReach = 4` gives steps 4→2→1 — three cheap passes. **Where you drop the resolve edge is the whole aesthetic:** feed `outlined` into bloom and the hover border *glows*; place it after bloom and it's crisp with no bleed.

### 7.6 The fast list (same shape, less detail)
- **SSAO / SSR** — a pass that `.Samples(depth, normal)` (plus `color` for reflections) and writes an occlusion/reflection term that `lighting` then samples. Inserting an effect *is* adding edges; ordering re-derives itself.
- **Clustered light culling** — a compute pass bins lights into a froxel-grid storage buffer; `lighting` reads the grid instead of a flat list. This is how deferred scales from a few lights to thousands.
- **Async compute** — tag a pass `.Queue(Queue.AsyncCompute)` and the graph overlaps it with graphics work, deriving the cross-queue timeline sync. The hardest synchronization in Vulkan, behind an enum.
- **GPU particles** — a compute pass writes a storage buffer; a draw pass reads it as vertex data. The graph emits the compute→vertex barrier.
- **Debug visualization** — every intermediate is a named handle, so blitting `normal`, `depth`, or `bloom_mip2` to the screen to inspect it is one `g.Pass(...).Blit(...)`. Cheap to add; you'll reach for it constantly.

---

## 8. Cross-cutting design principles

- **Derived, not declared.** The user states reads and writes; ordering, barriers, layout transitions, aliasing, and queue sync are computed. This is the whole thesis. Guard it — any API addition that asks the user to specify *order* is a smell.
- **`.Writes` vs `.Blends`.** `.Writes` = "I produce this; you may clear it." `.Blends` = "I accumulate onto its existing contents (LoadOp.Load + blend); I am both the last reader and next writer." The graph needs the distinction to derive correct barriers for accumulation passes.
- **Transient aliasing from lifetimes.** Resources whose lifetimes don't overlap share memory. Lifetimes are derived from first-write / last-read across the sorted graph. `g.Persistent` opts out.
- **The graph is built, not configured.** The `DefineGraph` closure is ordinary C#. Quality presets and feature toggles are *control flow* (`if (settings.Bloom) color = g.Bloom(...)`), not a config system and not branches inside shaders. Structural changes (toggling a pass) rebuild the baked graph — which is cheap and cached — so you rebuild when settings *change*, never per frame.
- **Per-frame `Draw`/`Dispatch` closures stay pure.** They pull `ctx.Scene` / `ctx.Camera` / `ctx.DeltaTime` from the execution context rather than capturing, so the graph description is frame-independent and bakeable/cacheable. This is the discipline that keeps the hot loop allocation-free.
- **Ping-pong via handle swap.** Two physical resources + a `(read, write)` tuple swap in a normal C# loop expresses an arbitrary-length ping-pong; the graph follows the handles and allocates only two buffers. Used by both bloom and JFA.
- **Backend isolation.** `Loom.Core` and `Loom.Graph` are backend-agnostic. Everything procedural and struct-heavy is quarantined in `Loom.Vulkan`. This separation is what made the dream tractable; protect it.

---

## 9. Pixel-art aesthetic decisions (collected)

These recur across effects, so they live in one place. They are *taste* layered on top of capability — the capabilities generalize; these choices are specific to the pixel-art target.

- **Low-res world domain + nearest upscale.** Render the entire 3D world at 320×180 and nearest-neighbor upscale to the screen. The upscale *is* the pixelation; it is not a post filter. Everything (geometry, lighting, transparency, bloom) happens at low res and is therefore pixel-consistent.
- **Bloom stays in the low-res domain.** Bloom after the upscale would smear soft glow over hard pixel edges and read as wrong. Computing it at 320×180 keeps glow chunky and locked to the pixel grid; the nearest upscale then makes each low-res texel a hard screen-pixel block.
- **Bloom mip count is near a ceiling, not a quality knob.** At 320×180 you run out of pyramid fast (mip4 ≈ 10×6; a 6th mip ≈ 5×3 is noise). `mips: 5` is about the most the resolution supports — the opposite of a full-res renderer's 8–9 deep chains.
- **Bloom prefilter resolution is a real choice.** Halving immediately (mip0 = 160×90) makes the smallest glow detail two world-texels — two screen pixels after upscale. For single-pixel-wide glow (crisper, "lit sprite" rather than "soft haze"), make mip0 full world resolution and only halve afterward. Costs one more full-res pass. (CrossCode-style crisp vs softer wash — prototype both; it's a one-line change.)
- **Bloom threshold hardness.** A soft knee makes mid-bright pixels bloom faintly, which can look muddy on a pixel grid. A harder threshold (only genuinely emissive texels bloom) reads as more intentional, as if the artist chose which pixels glow. Lives in the `BloomPrefilter` shader — a uniform tweak, not a graph change.
- **Skip PCF on shadows.** Hard-edged shadow maps look right for pixel art; PCF softening fights the aesthetic.
- **No TAA.** TAA spatially blurs to hide jitter, which smears hard pixel edges. Temporal *exposure* adaptation is safe because it only scales brightness, never blurs — so auto-exposure (7.3) is fine even though it uses the same history-buffer mechanism TAA would.
- **Quantize analytic SDFs for chunky borders.** HUD SDF borders are smooth by default. To match the pixel grid, quantize the distance (`d = floor(d)`) for stepped, pixel-locked rings instead of anti-aliased ones.
- **Outline domain choice.** Run the JFA outline in the world domain and the ring lands on the pixel grid and chunkifies on upscale (pixel-locked). Run the same passes in the screen domain for a smooth UI-style outline. One-line move.
- **HDR output is orthogonal (and engine-general).** True HDR display output (§3) is a capability the engine offers regardless of the pixel-art target — a pixel-art game can present HDR (chunky pixels, wide luminance) or stay SDR. It's an `EngineSettings.HdrOutput` choice, not an aesthetic baked into the pixel-art decisions here; like AA, it's a general-engine knob the demo doesn't have to use.

---

## 10. Open questions / decisions deferred

These are genuinely unresolved and should be decided as the relevant milestone is reached. Captured here so they aren't forgotten.

- **Memory allocator.** ✅ **RESOLVED (M4): VMA** — the pure-managed `VMASharp` port. The transient-resource aliaser builds on its placement/aliasing support.
- **Descriptor strategy.** Descriptor pools + descriptor *indexing* / bindless (Vulkan 1.2+), or per-pass push descriptors? SPIR-V reflection drives layouts either way. Bindless simplifies a lot but adds complexity up front. Decide at Milestone 1–2.
- **Bake cache hashing.** What composes the technique hash (shader SPIR-V hashes + all pipeline state) and the graph hash? Needs a stable, collision-resistant scheme. Decide at Milestone 1.
- **Frames-in-flight.** ✅ **RESOLVED (M4): 2.** Each transient gets one physical image per frame slot; the swapchain supplies distinct backbuffer images. (M7 lifts the count into `EngineSettings`.)
- **Shader pipeline.** ✅ **RESOLVED (M0/M1): Slang, compiled offline** via the bundled `slangc` (the `Slang.Sdk` NuGet, hermetic). Hot-reload remains a future cross-cutting workstream.
- **Reflection library.** ✅ **RESOLVED (M7): Slang's own reflection** — no extra dependency since Slang is already the compiler (`spirv-reflect`/SPIRV-Cross were the alternatives).
- **Graph (re)compilation cadence.** Many frame graphs rebuild the graph description cheaply *every frame* and cache only the pipelines. Loom's design caches the baked graph and rebuilds only on structural change. Confirm the description rebuild is cheap enough, or commit to the cache-and-invalidate model. Decide at Milestone 2–3.
- **Threading.** Multi-threaded command-buffer recording (secondary command buffers, or multiple primaries merged)? The Execute layer's record step is the place. Decide post-Milestone 5.
- **Culling ownership.** Currently `Scene` is app-owned and supplies draw lists (e.g. `SortedBackToFront`). Does Loom ever own frustum/occlusion culling, or stay strictly "you hand me draw lists"? Default: stay out of it. Revisit if clustered lighting / GPU-driven rendering lands.

### Decisions made in Milestone 7 (the engine review)

- **Front door.** ✅ A neutral `Engine`/`Loom` facade + `IRenderer` in Core/Graph; the host names no backend type. Backends self-register into a `Engine.AvailableEngines` registry and are picked at runtime by name; the build chooses which are present via `<UseVulkan>`/`<UseDX12>` (M8). The engine owns the long-lived device + pipeline cache and a *swappable active graph*.
- **Configuration.** ✅ A subclassable `EngineSettings` (validation, frames-in-flight, present/vsync, `HdrOutput`, `PreferredBackend`, …) with `VulkanEngineSettings`/`DX12EngineSettings` subclasses, replacing scattered env-var/constructor config.
- **Resource handles.** ✅ Full subtypes — `ColorHandle`/`DepthHandle`/`BufferHandle` (+ an `ITextureHandle` for sampleable inputs) — so verb misuse is a compile error.
- **Anti-aliasing.** ✅ Support **all three**, since the engine does *not* mandate deferred rendering: **MSAA** for forward/simple games (a describe-layer sample-count + a backend resolve), **post-process FXAA/SMAA** for deferred games (built-in graph-native techniques), and **none** (default). MSAA's describe-layer API lands in M7; the FXAA/SMAA + MSAA-resolve implementations in M9.
- **HDR output.** ✅ First-class (see §3) — negotiated swapchain colour space + format, an output transform, gated by `EngineSettings.HdrOutput`.
- **Debug tooling (M8).** `<UseImGui>`, `<UseDebugVisualization>` (per-resource ImGui windows; x64-only — the win-arm64 `ImVec2`-by-value HFA marshaling bug disables them on arm64), and `<UseNamedGPUObjects>` (object names + per-pass `vkCmdBeginDebugUtilsLabelEXT` scopes for RenderDoc).

---

## 11. Solution layout

```
Loom.sln
├── Loom.Core        // resource handles, GraphBuilder, technique builder (the DESCRIBE layer)
├── Loom.Graph       // topological sort, barrier derivation, transient aliasing (the EXECUTE layer)
├── Loom.Vulkan      // Silk.NET backend, the marshaling seam, bake-and-cache
├── Loom.Shaders     // .slang/.glsl + SPIR-V build step, reflection
└── Loom.Sandbox     // the deferred + bloom + pixelate + HUD frame as a runnable sample
```

Dependency direction: `Sandbox → Vulkan → Graph → Core`. `Loom.Core` and `Loom.Graph` carry **no** Vulkan/Silk.NET reference — they describe and schedule in the abstract. `Loom.Vulkan` is the only project that touches native structs. `Loom.Shaders` is a build step that produces SPIR-V (and reflection metadata) consumed at bake time.

---

## 12. Build roadmap

Milestones are ordered so each one validates a thesis before the next depends on it.

**Milestone 0 — Solution scaffold + cleared window.**
Create the `.sln` and five projects with the dependency graph above. Wire `Silk.NET.Vulkan`, `Silk.NET.Windowing`, `Silk.NET.Input`, `Silk.NET.Maths`. Target `net9.0`. Stand up an instance → physical device selection → logical device with the **Vulkan 1.3 features enabled** (dynamic rendering, synchronization2, timeline semaphores) → swapchain → a clear-to-color render loop using dynamic rendering (no renderpass object). *Proves the Silk.NET plumbing and the 1.3 baseline.*

**Milestone 1 — Marshaling seam + first triangle, hand-baked.**
Before any graph: prove the Bake layer. A minimal `TechniqueBuilder` that flattens to `VkGraphicsPipelineCreateInfo` via dynamic rendering, the transient pinned-arena marshaling pattern (establish the native-arena convention — `stackalloc` / pinned spans / `NativeMemory`, and prove no GC hazards), SPIR-V load, and a hardcoded fullscreen-triangle technique drawing a triangle. *Proves "the struct soup lives in the seam" on something tiny.* Lock in the bake-cache hashing here.

**Milestone 2 — Core describe layer.**
`ResourceHandle`, `Domain`, `GraphBuilder`, `PassBuilder` with the verbs (`.Writes` / `.Samples` / `.WritesDepth` / `.ReadsDepth` / `.Blends` / `.Draw`). Immutable and hashable. No execution yet — build an in-memory graph description, inspect/dump it, unit-test it. *Proves the authoring vocabulary independent of any GPU work.*

**Milestone 3 — Execute layer: scheduling + barriers.**
Topological sort from the read/write edges. **Barrier derivation** — the crux; port the approach from Arntzen's writeup, tracking per-resource last-usage state and emitting `vkCmdPipelineBarrier2` + image layout transitions. Transient allocation kept naive (one allocation per resource, no aliasing yet). Record and submit command buffers. Run a two-pass graph (render offscreen → blit to backbuffer) with the validation layers **clean**. *Proves derivation produces correct, stall-free GPU work.*

**Milestone 4 — Transient aliasing + frame pacing.**
Lifetime analysis (first-write/last-read per transient), memory aliasing (VMA or custom — decide here), multi-buffer the per-frame resources, timeline-semaphore frame pacing (2–3 frames in flight). Validate the G-buffer-aliases-bloom case once those passes exist. *Proves the memory and synchronization stories at scale.*

**Milestone 5 — The reference frame, built up incrementally.**
`gbuffer` → `lighting` (deferred) → forward `transparent` → the bloom sub-graph (`.Blends` accumulation) → nearest `upscale` (the pixelation) → `hud` with SDF borders → `present`. Then add auto-exposure (`Persistent` + `Compute`) and shadows (the `view:` param). This is the demo *and* the proof that the full vocabulary composes. Make it match the pixel-art decisions in Section 9. *Proves the whole design end to end.*

**Milestone 6 — The "prove it generalizes" layer (optional, pick by interest). ✅ chosen scope complete.**
Built: picking + GPU→CPU readback, JFA outlines composing with picking (the glowing-hover-border payoff), SSAO, GPU particles, debug viz, and a Dear ImGui HUD. The remaining optional items (clustered lighting, async compute, SSR) moved to **Milestone 9**.

**Milestone 7 — Comprehensive engine review + the front door.**
A whole-surface API audit before the engine grows further (the seven findings: no neutral front door; §5 drift; backend-shaped techniques; the five flagged items; missing features incl. HDR/AA/validation; DX12-portability hazards; one mega-demo). *Reconcile (A):* make §5 honest, scrub Vulkan-isms from Core's public docs, fix dead `Bloom` params + the picking format, add describe-layer validation, **full `ResourceHandle` subtypes**, `g.Screen` + resize-tracking domains, an HDR-capable backbuffer/format model, and a describe-layer **MSAA sample-count** API (the engine must not mandate deferred). *Front door (B):* a neutral `Engine`/`Loom` facade + `IRenderer` + a `Engine.AvailableEngines` backend registry (swappable active graph), `EngineSettings`(+`VulkanEngineSettings`), `FrameInputs` + `graph.Render(inputs)`, a describe-layer `Technique`/`DefineTechnique` builder with **Slang reflection**, the Vulkan NDC flip moved into the backend, the **HDR output transform**, and a **keyboard-switchable demo selector** (runtime graph-swap). *Proves the engine has a clean, backend-portable front door before more is built on it.*
> **Implementation status.** *Done:* all of **A** (reconcile/§5/validation/subtypes/`g.Screen`/MSAA-API); **B** front door — neutral `Engine` + `IRenderBackend` + `Engine.AvailableEngines` (self-registering via module initializer), `EngineSettings`/`VulkanEngineSettings`, `IEngine` with a **swappable active graph** (`SetGraph`), `FrameInputs` + `engine.Render(inputs)`, the **NDC flip moved into the backend**, neutral `TechniqueDescriptor` + `engine.DefineTechnique` (logical shader names — no backend type or `.spv` path in app code), **Slang reflection** deriving the technique descriptor-binding counts (Loom.Shaders emits `slangc -reflection-json`; the backend reads it at `DefineTechnique`, so the app states only pipeline intent — no hand-counted `SampleCount`), the **keyboard-switchable demo selector** (Triangle / Cube / Bloom / Full, swapped live), and the **HDR output thread** (A7/B6): `VK_EXT_swapchain_colorspace` + scRGB swapchain negotiation gated by `EngineSettings.HdrOutput`, with the tone-map preserving extended range for HDR vs the SDR Reinhard, falling back to SDR when HDR isn't offered (`LOOM_HDR=1` to request it). The DX12-portability bar is met: the host names no graphics-API type save the optional `VulkanEngineSettings`. **All M7 work is code-complete and validated headlessly** (build clean, 77 tests, validation-clean smoke across every demo, SDR frame pixel-unregressed); the one thing left to confirm is the **HDR *appearance* on an HDR display** (the negotiation logs `hdr=True` when scRGB is granted). HDR10/PQ output (a PQ encode in the terminal shader) is deferred — scRGB covers Windows HDR.

**Milestone 8 — Build system & debug tooling.**
`Loom.Shaders` becomes a `.targets` + a **Loom MSBuild SDK** (engine vs app shader split). `<UseVulkan>`/`<UseDX12>` build flags pick which backends register into `Engine.AvailableEngines` (default Vulkan; DX12 inert until that backend exists). `<UseImGui>` (engine-owned ImGui glue), `<UseDebugVisualization>` (per-resource ImGui buffer windows; x64-only, arm64 disabled with a label), and `<UseNamedGPUObjects>` (object names + per-pass debug-label scopes for RenderDoc).
> **Implementation status.** *Done (build clean, 79 tests, validation-clean smoke across every demo on Windows-on-ARM):* the **Loom MSBuild SDK** under `build/Loom.Sdk/` — `Sdk.props`/`Sdk.targets` (the app-facing SDK: shader compile + backend selection) and the shared `Loom.Shaders.props`/`Loom.Shaders.targets` (the hermetic slangc compile, two output modes), consumed by **explicit SDK-style `<Import>`s** (an app writes them as if it had declared `Sdk="Loom.Sdk"`; promotable to a real SDK later). The **engine-vs-app shader split**: the 6 engine-infrastructure shaders (tonemap/blit/fullscreen/exposure/imgui/debugview) compile to SPIR-V and **embed in `Loom.Vulkan`** (manifest `Loom.Vulkan.Shaders.<name>.{spv,json}`; the backend resolves embedded-first, with a disk fallback under `VulkanEngineSettings.ShaderDirectory` for the 15 app/demo shaders, which stay loose content with `Loom.Sandbox`). `TechniqueTemplate`/`TechniqueDescription` now carry the SPIR-V **bytes** (resolved once at `DefineTechnique`), and the **`Loom.Shaders` project is dissolved**. `<UseVulkan>`/`<UseDX12>` select backends from the app SDK (Vulkan default; `UseDX12=true` is inert via an `Exists()` guard + a warning, never an error; backend isolation is preserved by *absence of import* — Core/Graph/Tests never import the SDK). `<UseImGui>` gates ImGui end-to-end — off drops the ImGui.NET dependency from both engine and app, `#if`-excludes `ImGuiRenderer` + the embedded imgui shader, and the neutral `ctx.DrawImGui()`/`EnableImGui` seam in Core stays as a documented no-op. `<UseNamedGPUObjects>` enables `VK_EXT_debug_utils` on the flag (not just under validation), names images/views/buffers/command-buffers/pipelines, and wraps each pass in a `vkCmdBeginDebugUtilsLabelEXT` scope for RenderDoc — all via `[Conditional]` helpers, so calls (and their name interpolation) vanish when off. The feature flags are repo-wide in a root `Directory.Build.props`, so a flag recompiles the engine itself (the engine-as-source-in-the-game-repo model); the engine/app shader-compile targets and backend references come from explicit SDK imports, not auto-apply. *Deferred:* **`<UseDebugVisualization>`** (per-resource ImGui buffer windows) — it is x64-only and this dev box is arm64 (the win-arm64 `ImVec2`-by-value HFA marshaling bug disables it there), so it is deferred to an x64 session where the live windows can be runtime-verified; the arm64 build-time default (off) is already wired into `Directory.Build.props`.

**Milestone 9 — Deferred generalization.**
The deferred M6 optional items: clustered light culling, async compute (`.Queue`), SSR — plus the post-process AA implementations (FXAA/SMAA) and the MSAA resolve path (per M7's AA decision).

**Milestone 10 — DirectX 12 backend.**
The portability payoff: a second backend (`Loom.DX12`) slotting in behind the *unchanged* describe/execute layers, proving the §2/§8 backend-isolation invariant with real code rather than just intent. Built milestone-style, mirroring Vulkan's own M0/M1 before the graph executor is ported.
> **Implementation status.** *Done — four slices, on Windows-on-ARM (Adreno X1-85), D3D12 debug-layer clean (the backend now runs a real multi-pass render graph):* **Slice 1 (M0-equiv):** `DX12Backend : IRenderBackend` self-registers `"DX12"` via a module initializer (the `Create` seam takes the window's **HWND** opaquely — the DX12 counterpart of `IVkSurface`); `DX12Context` (DXGI factory + device on the default adapter + direct command queue + the debug layer and an `ID3D12InfoQueue` **drained each frame** — the analogue of the Vulkan validation messenger); `DX12Swapchain` (flip-model `IDXGISwapChain1`, RTV heap, manually-tracked backbuffer index — no `IDXGISwapChain3` needed); `DX12Engine : IEngine` (2-frame **fence pacing** matching Vulkan, `PRESENT`↔`RENDER_TARGET` barriers, clear, present). The host names no graphics-API type beyond `DX12EngineSettings`; a focused `Dx12Smoke` harness drives it (the demo gallery is graph-driven + Vulkan-surface-specific), built with `<UseDX12>` and picked at runtime by `LOOM_BACKEND=DX12`. **Slice 2 (M1-equiv):** the hand-baked triangle — an empty root signature + a graphics PSO whose VS fabricates the geometry from `SV_VertexID` (the same trick as the Vulkan fullscreen-triangle technique), with the HLSL compiled at runtime to **DXBC shader model 5.0 via the in-box FXC (`D3DCompile`)**. *Shader pipeline caveat:* the bundled `slangc` **cannot emit DXIL on win-arm64** (it needs `dxcompiler`/`dxil` downstream, neither shipped), so the **offline DXIL pipeline** (ship an arm64 dxc + dual-emit DXIL from the M8 shader SDK alongside SPIR-V) is a tracked follow-up; runtime FXC→DXBC keeps the slice dependency-free. *Bindings:* **Silk.NET.Direct3D12 / DXGI 2.23** — the same stack as `Loom.Vulkan`, reusing its marshaling idioms (`NativeArena`-style transient interop, opaque-surface seam). **Slice 3 (capture):** GPU→CPU readback un-stubs `RequestCapture` — copy the backbuffer to a READBACK buffer (256-aligned placed footprint), map, encode a PNG (a local dependency-free encoder); the verification tool the rest leans on. **Slice 4 (executor):** the substantive proof — `SetGraph` derives `GraphSchedule`/`BarrierPlan` (the *unchanged* neutral plans) and `Render` consumes them exactly as the Vulkan engine does: a per-pass walk emitting barriers, binding each pass's *derived* render target(s) from the `AttachmentAction`s (load actions honoured), running the record callback, then the final present transition. A DX12 `StateMapping` (`ResourceState`→`D3D12_RESOURCE_STATES`) and `Core.Format`→`DXGI_FORMAT`; transient colour targets (one per frame slot, no aliasing yet — like the Vulkan M3); a shader-visible descriptor ring + an SRV-table/static-sampler root signature for the sample path; PSOs baked per (technique, RT-formats). Verified end to end by a **two-pass offscreen→blit graph** (render a gradient into a transient, then sample it into the backbuffer) — the engine derives the inter-pass `COLOR_ATTACHMENT`→`SHADER_READ` barrier; PNG-captured, debug-layer clean. The from-scratch + sampling fullscreen verbs are implemented; mesh-draw/compute/push/ImGui throw `NotSupported`. *Note:* a key DX12-vs-Vulkan difference handled here — a D3D12 transition barrier needs the resource's *exact* current state, so the executor tracks real per-resource state and emits from it rather than mapping the plan's neutral `From` (Vulkan accepts `Undefined` as an old layout). **Slice 5 (depth + geometry):** the first *lit 3D* frame on DX12 — depth targets (a DSV heap + depth texture per frame slot in `DX12Transients`, the depth attachment bound and cleared in the executor, `DEPTH_WRITE`/`DEPTH_READ` from the existing neutral `DepthAttachment`/`DepthReadOnly` states), mesh upload + indexed draw (`DX12MeshStore`: vertex/index buffers in an upload heap; `CreateMesh`/`ctx.Draw`), and push constants as **root constants** (the 64-byte MVP at `b0`/vertex for a mesh, a 16-float scalar block at `b0`/pixel for a post pass — the technique cache now bakes the vertex input layout + depth state + the root-constants/SRV-table root signature, keyed per (technique, RT-formats, depth-format)). Verified by the DX12 twin of `CubeLightingDemo` (`LOOM_DX12_CUBE=1`): a lit cube + ground plane on a depth buffer, PNG-captured, debug-layer clean — upright and correctly depth-sorted, confirming D3D12's **identity clip-space ingest** (no Y-flip; +Y-up already) and that the raw row-major `Camera.ViewProjection` + FXC's column-major cbuffer packing reproduce the row-vector `clip = position·MVP` convention. **Slice 6 (compute + GPU particles):** the §7.6 vertical end to end — a **compute** pass (`ctx.Dispatch`) writes a storage **buffer** (a `RWByteAddressBuffer` bound as a **root UAV** by GPU address — DX12 root descriptors need no heap entry; the per-pass scalars ride a b0 root constant), a `DX12Transients` Default-heap UAV buffer per frame slot, then a draw reads that same buffer as a **per-instance vertex stream** (`ctx.DrawParticles` — 6 fabricated billboard verts × N instances) with **additive blend**; the technique cache gained the compute root-signature/PSO path, a per-instance input layout, and the blend-equation bake. The engine derives the **compute→vertex-input** hand-off barrier from the `.ReadWrites`→`.ReadsVertex` edges (DX12 transitions a buffer like any resource — `UNORDERED_ACCESS`↔`VERTEX_AND_CONSTANT_BUFFER` — unlike Vulkan's layout-free buffer memory barrier). Verified by the DX12 twin of `ParticlesDemo` (`LOOM_DX12_PARTICLES=1`): a glowing additive particle fountain over the lit scene, composited to the backbuffer via a fullscreen blit of an HDR (`Rgba16F`) target — PNG-captured, debug-layer clean. *Remaining:* transient **aliasing** (`AliasPlan` placed resources + aliasing barriers), `SamplePick` (readback/picking), ImGui, MSAA, and sampled-inputs/storage-images in a compute (auto-exposure/SSAO); the **offline DXIL shader pipeline** (built-in HLSL via runtime FXC is the interim shader source — real Slang→DXIL is the enabler for full demo parity, since the 21 Slang demo shaders don't yet run on DX12); explicit high-performance adapter selection; and HDR colour-space negotiation. With those, the DX12 backend reaches demo parity with Vulkan.

**Cross-cutting workstreams (alongside the above):** the shader build + reflection pipeline (Milestone 0–1 onward), the bake cache + hashing (from Milestone 1), and hot-reload (whenever iteration speed starts to hurt).

---

## 13. Glossary

- **Render graph** — the per-frame description of passes and their resource reads/writes, from which ordering, barriers, aliasing, and sync are derived.
- **Pass** — one unit of GPU work (raster or compute) with declared inputs/outputs. A *thread* in the loom metaphor.
- **Technique** — a material template: shader stages + pipeline state (blend/depth/raster) + vertex/instance layout. Baked to a `VkPipeline`, cached by hash.
- **Domain** — a resolution; all resources in a domain render at that size. The mechanism behind pixelation (world domain vs backbuffer domain).
- **Describe / Bake / Execute** — the three layers: author the description; flatten+create+cache Vulkan objects; sort+barrier+alias+record per frame.
- **The seam / marshaling seam** — the transient flatten-to-native-structs step at bake time; the only place procedural Vulkan code lives.
- **Transient resource** — a resource whose lifetime is bounded within a frame and whose memory can be aliased with other non-overlapping transients.
- **Persistent resource** — a resource that survives between frames; never aliased (e.g. exposure history).
- **`.Writes` / `.Blends`** — produce-(may-clear) vs accumulate-onto-(load+blend). The distinction barrier derivation needs for accumulation passes.
- **G-buffer** — the deferred-shading geometry buffer (albedo, normal, material, depth) consumed by the lighting pass.
- **Reverse-Z** — depth with a flipped projection and `GreaterEqual` compare, for better depth precision. Optional.
- **Bloom prefilter / Karis average** — the bright-pass + firefly-reducing average at the start of the bloom chain.
- **JFA (jump flood algorithm)** — a log₂(n)-pass GPU technique that propagates nearest-seed coordinates to build a distance field; used for world-space outlines.
- **Froxel** — a frustum-voxel; the 3D grid cell that clustered light culling bins lights into.
- **Timeline semaphore** — a monotonically increasing GPU/CPU sync primitive; the backbone of frame pacing and cross-queue sync.
- **Dynamic rendering** — the Vulkan 1.3 feature that removes `VkRenderPass`/`VkFramebuffer` objects; you begin/end rendering inline.