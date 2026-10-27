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

**Assets & materials** — image decode (StbImageSharp) with gamma-correct CPU-generated mips, including cube and `Rgba16F` HDR sources; glTF loading (SharpGLTF) that walks the node graph, bakes world transforms, and imports skins + animation clips; a `Material` multi-texture binding abstraction that fills a technique's reflected sampler slots. Meshes and textures can be released when the app is done with them: the handle dies at once and the GPU memory is freed once no frame in flight can read it.

**Geometry & instancing** — explicit packed vertex layouts (`MeshStandard`/`MeshSkinned`/`Overlay`/`Transform`); CPU-uploaded **and** GPU-compute-filled instancing; an optional `Scene` draw-list with CPU frustum culling.

**Skeletal animation** — GPU compute pre-skin (`ctx.DispatchSkin`): sample a clip into a bone palette, skin the bind pose once into a transient buffer, draw it with the ordinary forward technique.

**GPU particles** — a compute pass writes a storage buffer; a draw consumes it as an instance stream (the engine derives the compute→vertex barrier). Foliage instancing + wind rides the same path.

**2D / pixel-art** — sprite & tilemap batching, 2D deferred normal-mapped lighting, parallax layers, sprite-sheet animation, and a low-res world domain + nearest upscale (the upscale *is* the pixelation). Stylized toon/ramp lighting and a sky + day/night cycle.

**Debug & profiling spine** — per-pass GPU timing HUD (both backends), a resource / G-buffer channel viewer, shader/asset hot-reload, render-graph export (`Dump`/DOT/JSON), a detached debug camera + frustum freeze, and a Dear ImGui debug HUD.

**Draw throughput** — 10,000 *discrete* draws (own model matrix, own material — not one instanced call) render on both backends with **zero managed allocations per frame**. Materials keep their descriptor tables rather than rewriting them per draw, redundant pipeline/geometry re-binds are elided, each pass's barriers go in one submission, resource uploads batch into the frame instead of draining the GPU per resource, and compiled pipelines persist between runs. The `Draw Scale` demo is the workload; `DrawScaleTests` is the headless gate and prints µs/draw — reported, never asserted, since it moves with machine and driver.

**Game UI** — [`SUIsei`](external/SUIsei), a standalone WinUI-3-compatible, NativeAOT-clean XAML UI runtime, rendered through Loom's neutral draw-list path (the same one ImGui uses). Beyond controls and text it draws WinUI's shapes (`Rectangle`, `Ellipse`, `Line`, `Polyline`, `Polygon`, `Path` with path markup) with every stroke join, cap and dash, linear gradients on fills and strokes, `UIElement.Clip` and image brushes, as tessellated vector triangles antialiased over one pixel at any angle; a translucent stroke stays one colour where it crosses itself, and SUIsei's `suisei:StrokeTrim` and along-length stroke gradients animate draw-on reveals. The `Vector UI` demo shows them all from one XAML page, and the GPU is held to SUIsei's CPU rasterizer on that page. The UI demos are held to the same golden-image and cross-backend parity tests as the rest of the gallery, with a check that their UI actually reaches the frame, and the vertex and command structs SUIsei hands Loom without a copy are guarded field by field.

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

### Create and release resources between frames

`engine.CreateMesh` / `CreateTexture` upload once and hand back a stable handle; `engine.ReleaseMesh` / `ReleaseTexture` give it back. A release kills the handle at once — drawing, sampling or releasing it again throws and says it was released — and frees the GPU memory once every frame that may still read it has finished, so you can release a texture the frame after its last draw without waiting for the GPU. Ids are never reused, so a stale handle always fails loudly instead of naming whatever came next.

```csharp
// between frames: swap the image a pass samples (the pass reads _image live, as graph closures do)
var previous = _image;
_image = engine.CreateTexture(TextureData.FromRgba8(w, h, pixels, TextureFormat.Rgba8Srgb));
engine.ReleaseTexture(previous); // safe while frames that sampled it are still in flight
```

Two rules keep this honest:
- **Create and release between frames, never inside a pass.** A pass callback runs while the frame's commands are recorded; an upload made there only reaches the GPU ahead of the *next* frame, and a release could free what this frame already draws with. Both throw when called from a pass on the render thread (the `*Async` creators stay callable from other threads at any time). A producer that discovers what it needs while drawing resolves it in a step of its own before `engine.Render` — see `UIPainter.Prepare` below.
- **A texture a `Material` holds cannot be released.** A material keeps its textures for as long as it lives — the engine's lifetime — so `ReleaseTexture` throws and names the material. Sample a texture you mean to replace through a draw list.

### Draw a game UI

`Loom.UI.SUIsei`'s `UIPainter` draws a SUIsei host's composited surfaces through `ctx.DrawList`. Each frame, composite the host, then `Prepare` the painter — it uploads any image or glyph-atlas page the UI newly shows, before the frame that samples it, shares it between surfaces, and releases what the UI stopped showing — and draw from a pass:

```csharp
var ui = UIPainter.Create(engine, host);
g.Pass("hud").Blends(screen).Draw(ctx => ui.Draw(ctx, UISurface.Hud));

// each frame, before engine.Render:
host.Tick(dt, time, size);
host.Render();
ui.Prepare();
```

Several hosts can draw in one frame (a HUD plus an in-world panel): each painter keeps its surfaces' geometry under draw-list streams it mints with `engine.AllocateDrawListStream()`, so their surface ids, which every host numbers from zero, never collide. See [`Loom.UI.SUIsei/README.md`](Loom.UI.SUIsei/README.md).

UI can follow the world two ways. `InWorldPanel` draws a UI tree into a texture on a quad in the scene. `ScreenAnchors` keeps screen-space elements — labels, markers — pinned to world points: each frame it moves them to where the frame's view-projection puts their points, and fades out the ones whose points leave the viewport or go behind the eye. Give the anchors a view of their own, since they change every frame and SUIsei recomposites a whole view when anything in it changes:

```csharp
var anchors = ScreenAnchors.Create(engine, host.CreateRoot(new UILayer(1)));
anchors.Add(new TextBlock { Text = "Start" }, startLine, alignment: new(0.5f, 1f), pixelOffset: new(0f, -8f));
g.Pass("labels").Blends(screen).Draw(ctx => ui.Draw(ctx, anchors.View.Surface));

// each frame: lay out, place, composite
host.Tick(dt, time, size);
anchors.Update(camera.ViewProjection, size, dt);
host.Render();
ui.Prepare();
```

The projection under it is plain matrix maths in `Loom.Core.Geometry` — `viewProjection.TryProjectToScreen(world, size, engine.ClipSpaceCorrection, out var pixel, out var depth)` — which reads the engine's clip-space correction to put a point on the same pixel on every backend.

### Show a render target in a draw list

A draw list's textures are `TextureRef`s: an engine `Texture`, or a graph texture another pass rendered this frame — a minimap, a portal view, a preview. The pass that submits the list declares the target with `.Samples` like any sampled input, which orders it after the target's producer and makes it readable; a list naming an undeclared target throws `PassBindingException`, and a depth or integer target is refused because a list samples through a filtering float sampler.

```csharp
var map = g.Domain("map", 256, 256).Color("map.color", Format.Rgba8Srgb);
g.Pass("map").Reads(mapViewProj).Writes(map).Draw(ctx => DrawTopDown(ctx));
panel.Textures[1] = map;                                   // TextureRef[], rewritten in place
g.Pass("minimap").Samples(map).Blends(screen).Draw(panel, static (ctx, p) => p.Draw(ctx));
```

A SUIsei page shows one the same way: a `HostImageSource` names it by tag, `UIPainter.SetExternalTexture(tag, handle)` binds the tag to the graph texture, and the pass that draws the UI declares it with `.Samples`. The gallery's *UI Live Texture* demo shows a minimap this way, beside a `WriteableBitmap` it redraws on the CPU every frame. The painter replaces that bitmap's texture on each change and releases the old one past the frames in flight; updating a texture in place is a labelled gap. Images read as their sources declare — sRGB decoded before filtering, straight alpha premultiplied after it — exactly as SUIsei's software rasterizer reads them. See [`Loom.UI.SUIsei/README.md`](Loom.UI.SUIsei/README.md).

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
- Guides: [getting started](docs/getting-started.md) · [API vocabulary](docs/api-vocabulary.md) · [shader authoring](docs/shader-authoring.md) · [effect cookbook](docs/effect-cookbook.md) · [architecture](docs/architecture.md) · [porting from OpenGL](docs/porting-from-opengl.md)
- [CI with a software rasterizer (WARP, no GPU)](docs/ci-software-rasterizer.md)
- [WebGPU pixel parity](docs/webgpu-parity.md)
