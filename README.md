# Loom

A declarative, render-graph-driven rendering engine for .NET 10, running on **Vulkan and DirectX 12 at pixel parity**.

You declare *what* each render pass reads and writes; Loom derives ordering, barriers, image-layout transitions, transient memory aliasing, and CPU↔GPU synchronisation. The procedural, struct-heavy graphics-API code is quarantined in a backend layer and never leaks into the authoring API.

> **Docs map.** This README is *what Loom does and how to use it.* [`Planning.md`](Planning.md) is the design bible and forward roadmap. `CLAUDE.md` is the operational guide. The UI track has its own bible at [`external/SUIsei/Planning.md`](external/SUIsei/Planning.md).

## Implemented features

**Core render graph**
- Declare reads/writes per pass; the engine derives pass ordering, every barrier and image-layout transition, transient-memory aliasing (non-overlapping lifetimes share memory), and frame pacing.
- Immutable, hashable graph + technique descriptions, baked and cached by hash.
- Reusable sub-graphs compose as plain extension methods, and `using (g.Scope("left"))` instantiates one twice in a graph without the fragment taking a prefix parameter; ping-pong is two handles swapped in a normal loop.
- GPU→CPU readback as a first-class edge (`g.Readback`), resolving 1–2 frames later.

**Two backends at pixel parity**
- **Vulkan 1.3** — dynamic rendering (no renderpass/framebuffer objects), synchronization2, timeline semaphores.
- **DirectX 12** — PSOs, fences, placed-resource aliasing, signed offline DXIL.
- An automated parity test plus golden-image regression keep the two backends pixel-identical.

**Lighting & shadows** — deferred G-buffer + multi-light lighting; forward alpha-blend transparency; single shadow map **and** cascaded shadow maps (per-pixel cascade selection); SSAO + blur; tangent-space normal mapping.

**Post & HDR** — bloom pyramid (Karis-averaged downsample, tent upsample); auto-exposure (cross-frame `Persistent` + compute) + tonemap; HDR (scRGB) display output; JFA world-space outlines + GPU→CPU cursor picking.

**Assets & materials** — image decode (StbImageSharp) with gamma-correct CPU-generated mips, including cube and `Rgba16F` HDR sources; glTF loading (SharpGLTF) that walks the node graph, bakes world transforms, and imports skins + animation clips; a `Material` multi-texture binding abstraction that fills a technique's reflected sampler slots.

**Geometry & instancing** — explicit packed vertex layouts (`MeshStandard`/`MeshSkinned`/`Overlay`/`Transform`); CPU-uploaded **and** GPU-compute-filled instancing; an optional `Scene` draw-list with CPU frustum culling.

**Skeletal animation** — GPU compute pre-skin (`ctx.DispatchSkin`): sample a clip into a bone palette, skin the bind pose once into a transient buffer, draw it with the ordinary forward technique.

**GPU particles** — a compute pass writes a storage buffer; a draw consumes it as an instance stream (the engine derives the compute→vertex barrier). Foliage instancing + wind rides the same path.

**2D / pixel-art** — sprite & tilemap batching, 2D deferred normal-mapped lighting, parallax layers, sprite-sheet animation, and a low-res world domain + nearest upscale (the upscale *is* the pixelation). Stylized toon/ramp lighting and a sky + day/night cycle.

**Debug & profiling spine** — per-pass GPU timing HUD (both backends), a resource / G-buffer channel viewer, shader/asset hot-reload, render-graph export (`Dump`/DOT/JSON), a detached debug camera + frustum freeze, and a Dear ImGui debug HUD.

**Draw throughput** — 10,000 *discrete* draws (own model matrix, own material — not one instanced call) render on both backends with **zero managed allocations per frame**. Materials keep their descriptor tables rather than rewriting them per draw, redundant pipeline/geometry re-binds are elided, each pass's barriers go in one submission, resource uploads batch into the frame instead of draining the GPU per resource, and compiled pipelines persist between runs. The `Draw Scale` demo is the workload; `DrawScaleTests` is the headless gate and prints µs/draw — reported, never asserted, since it moves with machine and driver.

**Game UI** — [`SUIsei`](external/SUIsei), a standalone WinUI-3-compatible, NativeAOT-clean XAML UI runtime, rendered through Loom's neutral draw-list path (the same one ImGui uses).

## How to use it

### Author a render graph

A graph is built once with `RenderGraph.Define` (it's immutable and cached); you set it on the engine with `engine.SetGraph`, then call `engine.Render(frameInputs)` each frame. You declare each pass's resources and read/write intent — never the order or the barriers. **Loom owns no scene**; you submit your own draw list:

```csharp
var graph = RenderGraph.Define(g =>
{
    var screen   = g.Backbuffer;                              // the swapchain target
    var depth    = g.Screen.Depth("depth", Format.Depth32F);
    var viewProj = g.Constants<Matrix4x4>("camera.viewproj"); // a per-frame uniform channel

    g.Pass("opaque")
        .Reads(viewProj)             // declared inputs/outputs — ordering & barriers are derived
        .Writes(screen)
        .WritesDepth(depth)
        .Draw(ctx =>
        {
            foreach (var obj in visibleObjects)        // your own culled draw list, however you build it
            {
                var call = DrawCall.Create(obj.Mesh, "lit");
                call.Material = obj.Material;
                var model = obj.ModelMatrix;
                call.SetUniforms(ref model);           // a per-draw block, matching the shader's cbuffer
                ctx.Draw(call);
            }
        });

    g.Present(screen);
});
engine.SetGraph(graph);
```

### The pass-builder verbs

These are the whole scheduling vocabulary — you declare intent and the engine derives ordering, barriers, and layout transitions:

| Verb | Meaning |
|---|---|
| `.Writes(r)` | produce a colour target (the engine may clear it) |
| `.Blends(r)` | read-modify-write: load the existing contents + blend (accumulate) |
| `.Samples(r)` | bind a resource as a shader-read (sampled) input |
| `.WritesDepth(d)` / `.ReadsDepth(d)` | depth write / depth test without write |
| `.ReadWrites(r)` | storage read-modify-write (compute) |
| `.ReadsVertex(b)` | read a buffer as a vertex / instance stream |
| `.ReadsIndirect(b)` | read a buffer as draw arguments (counts the GPU wrote, not the CPU) |
| `.Reads(c)` | read a constants buffer (an app-filled uniform) |
| `.Reads(b)` | read a read-only storage buffer / SSBO (an overload of `.Reads`) |
| `.Queue(q)` | assign the pass to a queue — `Queue.AsyncCompute` throws at derive time until multi-queue scheduling lands, rather than pretending an overlap that never happens |

Resolution **`Domain`s** decide where each resource lives: `g.Screen` (native size), `g.Backbuffer`, or a fixed `g.Domain("world", 320, 180)`. A fixed low-res world domain plus a nearest-neighbour upscale is the pixel-art look.

### Feed per-frame data (the engine names no game concepts)

`FrameInputs` carries only `DeltaSeconds` and `Time` — there is no engine `Camera` or `Scene`. View, light, and time-of-day data flow in through neutral `Constants`/`Storage` buffers your passes `.Reads`:

```csharp
// each frame, before engine.Render(...): publish the matrix the "opaque" pass reads
engine.Write(viewProj, camera.ViewProjection);   // the camera helper folds in the clip-space correction
```

(`Camera`, `Frustum`, and `Scene` are app-side helpers in `Loom.Concepts` — convenience, not part of the engine.)

### Define a technique

A technique names its shader *logically* (each backend resolves its own SPIR-V/DXIL) and states its pipeline intent (blend, cull, vertex layout). The descriptor binding counts are reflected from the shader, so you never hand-count them:

```csharp
engine.DefineTechnique("lit", new TechniqueDescriptor("lit", HasVertexInput: true));

// a richer technique: instanced, back-face culled, drawn from the Transform per-instance layout
engine.DefineTechnique("foliage", new TechniqueDescriptor("foliage",
    HasVertexInput: true,
    Instance: VertexLayout.Transform,
    Cull: CullMode.Back));
```

`TechniqueDescriptor` options:

| Option | Purpose |
|---|---|
| `Shader` | logical shader name; the backend resolves bytecode + reflection |
| `VertexEntry` / `FragmentEntry` | entry-point names (default `vertexMain` / `fragmentMain`) |
| `HasVertexInput` | reads a vertex stream (a mesh) vs fabricating a fullscreen triangle |
| `IsCompute` | a compute technique — one entry point, dispatched not drawn |
| `Layout` | the per-vertex `VertexLayout` (defaults to `MeshStandard`) |
| `Instance` | the per-instance `VertexLayout` (e.g. `Transform`) for instanced draws |
| `Blend` | blend equation (alpha-over, additive, …) — pairs with a `.Blends` pass |
| `Cull` | which faces to discard — `None` / `Back` / `Front` |
| `LinearFilter` | sample inputs with linear vs nearest filtering |
| `Address` | sampler wrap mode — `Clamp` / `Repeat` / … |
| `HasFragmentPush` | a small inline per-pass scalar block (JFA step, exposure rate, …) |

Shaders are authored in **Slang** and compiled offline by the Loom shader SDK to SPIR-V (Vulkan) and signed DXIL (DirectX 12).

### Draw, instance, skin

`ctx.Draw(in DrawCall)` is the unified draw verb. Build a call with `DrawCall.Create(mesh, technique)` or an object initializer, then set what you need: `.Material` (its textures fill the technique's reflected sampler slots), `.SetUniforms(value)` (a per-draw block), or instances — `DrawCall.From(span)` for CPU instances, `DrawCall.Gpu(buffer, count)` for a buffer a compute pass filled. Skinning is `ctx.DispatchSkin(...)` (a compute pre-skin into a `.ReadsVertex` buffer) followed by an ordinary draw of that buffer.

`.Indices = DrawCall.Range(firstIndex, indexCount, baseVertex)` draws one sub-range of a mesh, so a model's primitives can share a single vertex and index buffer instead of becoming a mesh each. The default is the whole mesh.

`.Instances = DrawCall.Indirect(buffer, offset)` takes the counts from a buffer instead of the CPU — the pass declares it `.ReadsIndirect`, and the engine derives the compute→draw hand-off exactly as it does for a compute-filled instance stream. (Count buffers, multi-draw, and GPU culling are not built; this is the verb they would need.)

### Build & run

```bash
dotnet build Loom.slnx                          # build everything
dotnet run   --project Loom.Sandbox             # interactive demo gallery (opens a window, blocks until closed)
dotnet run   --project Loom.Sandbox -- --demo bloom --backend dx12   # pick a demo + backend
dotnet run   --project Loom.Tests               # unit tests (TUnit) — incl. GPU-gated golden/parity over the gallery
```

The sandbox is a `LoomApplication` on the desktop host: every run is interactive. Deterministic, headless rendering verification is in `Loom.Tests` — `GalleryGoldenTests`/`GalleryParityTests` render the gallery offscreen in-process (GPU-gated; skipped without one). `Scripts/Verification/` has `colorcount.ps1` (did anything render?) and `diff.ps1` (pixel diff + heatmap) for ad-hoc PNG inspection.

## Packages

| Package | Role |
|---|---|
| `Loom` | Describe layer — `GraphBuilder`, resource handles, `Domain`, technique & vertex-layout builders, `Material`, neutral draw-list types |
| `Loom.Graph` | Execute layer — scheduling, barrier/layout/aliasing derivation (backend-neutral) |
| `Loom.Assets` | Neutral image (StbImageSharp) + glTF (SharpGLTF) loaders |
| `Loom.Backend.Vulkan` | Vulkan 1.3 backend |
| `Loom.Backend.DirectX12` | DirectX 12 backend |
| `Loom.Backend.WebGPU` | WebGPU backend (browser) |
| `Loom.Concepts` | App-side helpers — `Camera`, `Frustum`, `Scene`. Not part of the engine |
| `Loom.Input` | Neutral input: actions, bindings, layered event routing |
| `Loom.Hosting.Desktop` | Desktop host — window, surface, frame loop, CLI flags |
| `Loom.Hosting.Web` | Browser host, paired with the WebGPU backend |
| `Loom.UI.SUIsei` | SUIsei XAML-UI integration (via the neutral draw-list verb) |
| `Loom.UI.ImGui` | Dear ImGui integration |
| `Loom.Sdk` | MSBuild SDK — reference this from a game project |


Consuming Loom selects backends through the SDK:

```xml
<PackageReference Include="Loom.Sdk" Version="0.1.0-preview.1" />
```
```xml
<UseVulkan>true</UseVulkan>
<UseDirectX12>false</UseDirectX12>
```

## Architecture

Three layers, kept strictly separate — `Sandbox → {backends} → Graph → Core`:

- **Describe** (`Loom.Core`) — immutable, hashable, fluent builders; the only layer authored against directly.
- **Execute** (`Loom.Graph`) — derives barriers, layout transitions, and aliasing; backend-neutral, no graphics-API types.
- **Bake + record** (the backends) — the marshaling seam: native structs, cached pipelines, real GPU work.

`Loom.Core` and `Loom.Graph` carry no Vulkan/DX12/Silk.NET reference — that isolation is what keeps the engine backend-portable (and is why a Metal or WebGPU backend can be added without touching authoring code). See [`Planning.md`](Planning.md) for the design rationale, the Pillars of Design, and the roadmap.

## Links

- [GitHub](https://github.com/AtomicBlom/Loom)
- [Design bible & roadmap](Planning.md) · [UI track](external/SUIsei/Planning.md)
- Guides: [getting started](docs/getting-started.md) · [API vocabulary](docs/api-vocabulary.md) · [shader authoring](docs/shader-authoring.md) · [effect cookbook](docs/effect-cookbook.md) · [architecture](docs/architecture.md)
- [CI with a software rasterizer (WARP, no GPU)](docs/ci-software-rasterizer.md)
