# Loom API Vocabulary

A reference for every key type in Loom's public API — *how to use it*. You author against `Loom.Core` (the Describe layer); the engine derives ordering, barriers, layout transitions, and aliasing for you. Verbs read as intent — you state what a pass reads and writes, never the order.

## Graph description (`Loom.Core.Graph`)

A graph is built once with `RenderGraph.Define(g => { … })` and set on the engine with `engine.SetGraph`. The closure receives a `GraphBuilder` (`g`); you declare domains, resources, and passes on it.

| Type / member | Role |
|---|---|
| `RenderGraph.Define(g => …)` | The one entry point: runs the authoring closure and returns an immutable, hashable `RenderGraph`. Hand it to `engine.SetGraph`. |
| `g.Pass("name")` | Begin a raster pass → `PassBuilder`. Chain access verbs, then `.Draw(ctx => …)`. |
| `g.Compute("name")` | Begin a compute pass → `PassBuilder`. Chain access verbs, then `.Dispatch(ctx => …)`. |
| `g.Present(colorHandle)` | The terminal: declare which colour resource is shown this frame (returns void). |
| `g.Readback(handle)` | Declare a GPU→CPU readback of a resource; returns the same handle. Read it later with `engine.Read`. |
| `g.Buffer("name", byteSize)` | A GPU storage buffer of `byteSize` bytes (not resolution-bound) → `BufferHandle`. |
| `g.Constants<T>("name")` | An app-written uniform/constant block of `sizeof(T)` bytes → `ConstantsHandle`. Fill with `engine.Write`. |
| `g.Storage<T>("name", count, growable=false)` | An app-written structured array (SSBO), `count × sizeof(T)` → `StorageBufferHandle`. `growable: true` lets a larger `Write` grow it. |
| `g.Persistent("name", format, size=1)` | A cross-frame colour image whose memory is never aliased → `ColorHandle`. Only `size == 1` is supported. |

### Pass access declarations (`PassBuilder`, all chainable)

You declare intent; the engine derives ordering, barriers, and layout transitions.

| Verb | Meaning |
|---|---|
| `.Writes(params ColorHandle[])` | Produce colour target(s) (the engine may clear). Multiple handles = MRT. |
| `.Blends(ColorHandle)` | Read-modify-write: load existing contents + blend onto them (the pass is last reader and next writer). |
| `.Samples(params ITextureHandle[])` | Bind colour/depth image(s) as shader-read (sampled) inputs. |
| `.WritesDepth(DepthHandle)` / `.ReadsDepth(DepthHandle)` | Depth write / depth test without write. |
| `.ReadWrites(params IStorageHandle[])` | Read-modify-write storage images or buffers (compute UAV). |
| `.ReadsVertex(BufferHandle)` | Bind a buffer as a vertex / instance stream (a compute pass filled it). |
| `.Reads(IUniformHandle)` | Read a `g.Constants` block as the technique's uniform input. |
| `.Reads(IStorageBufferHandle)` | Read a `g.Storage` SSBO read-only (an overload of `.Reads`). |
| `.Queue(Queue)` | Assign the pass to a queue (see `Queue` below). |
| `.Draw(Action<IRenderContext>)` / `.Dispatch(Action<IRenderContext>)` | The per-frame record callback (raster / compute). Pull from `ctx`; never capture per-frame data. |

### Special handles & domains

`Domain`s decide where each resource lives. Declare a colour/depth target *on* a domain — there is no standalone `g.Color`.

| Member | Description |
|---|---|
| `g.Backbuffer` | The swapchain colour target (`ColorHandle`) — *is* the present surface. Hand it to `.Writes`/`.Blends` and `g.Present`. |
| `g.Screen` | The native-resolution `Domain` (alias for `g.Backbuffer.Domain`); declare full-res sibling targets here. |
| `g.Domain("name", width, height, samples=1)` | A fixed-size `Domain` (a constant-size pixel-art world); `samples` is the MSAA count (power of two). |
| `g.Domain("name", scale, samples=1)` | A backbuffer-relative `Domain` — `scale × native`, re-resolved on resize (e.g. `0.5f` for half-res). |
| `domain.Color("name", format, clearValue?=null, noClear?=false)` | A colour target in this domain → `ColorHandle`. `noClear: true` skips the clear (only when the first pass covers every pixel). |
| `domain.Color("name", format, scale, …)` | A colour target at a fraction of the domain's resolution (mip chains, bloom pyramid). |
| `domain.Depth("name", format)` | A depth target in this domain → `DepthHandle`. |
| `domain.Persistent("name", format)` | A cross-frame target sized to this domain (use a tiny domain for a scalar history). |
| `g.BackbufferClear` / `g.BackbufferNoClear` | Set the backbuffer's first-write clear colour / skip the clear — before the first `g.Backbuffer` access. |

So a target is declared `g.Screen.Color("hdr", Format.Rgba16F)` or `g.Screen.Depth("depth", Format.Depth32F)`.

### `Format` (render-graph attachment formats, `Loom.Core.Graph`)

The complete set of attachment/target formats:

| Format | Typical use |
|---|---|
| `Rgba8Srgb` | sRGB colour the eye reads directly (the default backbuffer encoding). |
| `Rgba8Unorm` | 8-bit RGBA linear data (packed material params). |
| `Rgb10A2` | 10/10/10/2 packed — cheap higher-precision normals. |
| `Rgba16F` | HDR colour with range for bloom. |
| `R16F` | Single-channel float — SSAO, the exposure scalar, small object-id picking. |
| `R32Uint` | Single-channel 32-bit uint — exact integer object ids of any range. |
| `Rg16F` | Two-channel float — velocity, JFA seed coordinates. |
| `Depth32F` | 32-bit float depth. |

### `Queue` (`Loom.Core.Graph`)

`Queue.Graphics` (the default for every pass) and `Queue.AsyncCompute`. `AsyncCompute` is reserved for a future multi-queue scheduler — assigning it currently fails loudly at derive time rather than silently running on the graphics queue.

## Authoring a graph

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
                call.SetUniforms(obj.ModelMatrix);     // a per-draw block, matching the shader's cbuffer
                ctx.Draw(call);
            }
        });

    g.Present(screen);
});
engine.SetGraph(graph);
```

Ping-pong (bloom, JFA) is just two handles and a `(read, write)` tuple swapped in a normal loop — the graph follows the handles. View, light, and time data flow in through `g.Constants`/`g.Storage` channels a pass `.Reads`; the engine names no game concept.

## Engine hosting (`Loom.Core.Hosting`)

| Type | Role |
|---|---|
| `Engine` | Static backend registry. `Engine.AvailableEngines` is an `IReadOnlyList<IRenderBackend>` (registration order); each referenced backend assembly self-registers as it loads. |
| `IRenderBackend` | A discoverable backend: `string Name` and `IEngine Create(object surface, int width, int height, EngineSettings settings)`. The surface is opaque (Vulkan `IVkSurface` / DX12 `HWND`). |
| `IEngine` | The live engine — owns the device and pipeline cache, survives graph swaps. See its members below. |
| `IRenderContext` | The per-frame execution context passed to `.Draw` / `.Dispatch`. Closures *pull* from it rather than capturing. |
| `EngineSettings` | Backend-neutral engine config (subclass per backend). |
| `FrameInputs` | The per-frame host data handed to `IEngine.Render`. |
| `TechniqueDescriptor` | A backend-neutral technique definition (`Loom.Core.Materials`), registered with `IEngine.DefineTechnique`. |

### Selecting a backend and creating the engine

```csharp
var backend = Engine.AvailableEngines.FirstOrDefault(e => e.Name == settings.PreferredBackend)
              ?? Engine.AvailableEngines.First();
using var engine = backend.Create(window.Surface, width, height, settings);
```

`Engine.AvailableEngines` is a list, not a dictionary — select by `Name`, never an indexer. Constructing a backend's `EngineSettings` subclass (`VulkanEngineSettings` / `DirectX12EngineSettings`) is what loads and registers that backend.

### `EngineSettings`

| Member | Purpose |
|---|---|
| `EnableValidation` | Enable the backend's validation/debug layer (on by default; degrades gracefully if absent). |
| `VSync` | Paced, tear-free present; when false the backend uses its lowest-latency present mode. |
| `MaxFramesPerSecond` | Frame-rate cap (0 = unlimited; only meaningful with `VSync` off). |
| `HdrOutput` | Request an HDR-capable swapchain + HDR output transform, falling back to SDR. |
| `PreferredBackend` | The backend `Name` to select (null = the first available). |
| `WatchShaderDirectory` | Hot-reload loose shaders rebuilt on disk (dev-only; off by default). |

`VulkanEngineSettings` adds `ShaderDirectory` and `PreferDiscreteGpu`. `DirectX12EngineSettings` adds `ShaderDirectory`, `PreferDiscreteGpu`, and `GpuBasedValidation` (on in Debug, off in Release).

### `IEngine` members

| Member | Description |
|---|---|
| `BackendName` / `DeviceName` | The backend name (e.g. `"Vulkan"`) and a human-readable GPU name. |
| `DefineTechnique(name, TechniqueDescriptor)` | Register a technique under a logical name passes reference. Idempotent per name. |
| `CreateMesh(ReadOnlySpan<Vertex>, ReadOnlySpan<uint> indices)` | Upload geometry in the canonical `MeshStandard` layout → `Mesh`. |
| `CreateMesh<T>(ReadOnlySpan<T>, VertexLayout, ReadOnlySpan<uint>)` | Upload geometry with an explicit layout (`sizeof(T) == layout.Stride`). |
| `CreateSkinnedMesh(ReadOnlySpan<SkinnedVertex>, ReadOnlySpan<uint>)` | Upload a skinned bind-pose mesh (readable by the compute pre-skin) → `Mesh`. |
| `CreateTexture(TextureData)` | Upload an image + mip chain → `Texture`. |
| `CreateMaterial(params ReadOnlySpan<Texture>)` | Bundle textures (in the shader's binding order) → `Material`. |
| `SetGraph(RenderGraph)` | Make a graph active — derive its schedule/barriers/aliasing, reusing device + pipeline cache. Safe to call again to swap graphs. |
| `Write<T>(ConstantsHandle, in T)` | Write a value into a `g.Constants` buffer (reaches the reading pass on the next frame). |
| `Write<T>(StorageBufferHandle, ReadOnlySpan<T>)` | Write an array into a `g.Storage` buffer (the span counterpart). |
| `Render(FrameInputs)` | Render one frame of the active graph. |
| `Resize(int width, int height)` | Notify the engine the output surface resized (drives swapchain + relative-domain recreation). |
| `Read(ResourceHandle)` | Read back a `g.Readback` target's CPU copy as tightly-packed bytes (a frame or two old; empty before populated). |
| `RequestCapture(string path)` | Capture the next presented frame to a PNG (debug/validation). |
| `GpuTimingEnabled` | Toggle per-pass GPU timestamp collection (off by default; no graph rebuild). |
| `LastFrameTimings` / `LastFrameGpuMilliseconds` | Per-pass timings and total GPU time of the most recently completed frame. |
| `ResourceInspectionEnabled` / `InspectableResources` | Enable the resource viewer (set before `SetGraph`) and list the inspectable images. |
| `WaitIdle()` | Block until the GPU is idle (call before disposing host-owned resources). |

### `IRenderContext` members

| Member | Description |
|---|---|
| `Time` / `DeltaTime` | Elapsed seconds since startup / since the previous frame. |
| `HdrOutput` | `true` when the engine negotiated an HDR display output. |
| `FullScreen(technique, params ReadOnlySpan<ResourceHandle> inputs)` | Draw a fullscreen triangle, binding `inputs` as the technique's sampled inputs 0..N-1. |
| `FullScreen(technique, ReadOnlySpan<float> push, params ReadOnlySpan<ResourceHandle> inputs)` | As above, plus a small inline fragment-stage scalar block. |
| `Dispatch(technique, (uint X, uint Y, uint Z) groups, ReadOnlySpan<float> push, ReadOnlySpan<ResourceHandle> sampled, ReadOnlySpan<ResourceHandle> storage)` | Dispatch a compute technique over a workgroup grid. |
| `Draw(in DrawCall call)` | Submit one raster draw (the unified draw verb — see `DrawCall`). |
| `DispatchSkin(technique, Mesh skinnedMesh, ReadOnlySpan<Matrix4x4> palette, BufferHandle output, int vertexCount)` | Compute pre-skin a bind pose by a bone palette into `output` (a `.ReadWrites` buffer). |
| `DrawList<TVertex>(technique, ReadOnlySpan<Texture> textures, ReadOnlySpan<TVertex> vertices, ReadOnlySpan<ushort> indices, ReadOnlySpan<DrawCommand> commands, ReadOnlySpan<float> push)` | Submit a per-frame batched indexed draw list (the overlay/UI path). |
| `ResolveInspectable(int resourceId)` | Resolve an inspectable resource id to a `Texture?` bindable in a draw list this frame. |
| `DrawLists` | This frame's draw lists — `IReadOnlyList<(DrawListId Id, DrawListData Data)>` the host supplied via `FrameInputs`. |

### `FrameInputs`

A fresh immutable record per frame. The single channel for everything frame-varying — there is no engine `Camera`/`Sun`/`Scene`:

| Member | Description |
|---|---|
| `DeltaSeconds` (`double`) | Seconds since the previous frame; surfaced as `ctx.DeltaTime`. |
| `Time` (`float`) | Elapsed seconds since startup; surfaced as `ctx.Time`. |
| `DrawLists` | `IReadOnlyList<(DrawListId, DrawListData)>` of composited overlay/UI lists (default empty). |

View-projections, light, and time-of-day data are *not* here — publish them through `g.Constants`/`g.Storage` with `engine.Write(handle, value)` and read them in a pass with `.Reads`.

### `TechniqueDescriptor` (`Loom.Core.Materials`)

A record naming a shader *logically* and stating its pipeline intent; descriptor-binding counts are reflected from the shader, so you never hand-count them.

```csharp
engine.DefineTechnique("lit", new TechniqueDescriptor("lit", HasVertexInput: true));

// instanced, back-face culled, drawn from the Transform per-instance layout
engine.DefineTechnique("foliage", new TechniqueDescriptor("foliage",
    HasVertexInput: true,
    Instance: VertexLayout.Transform,
    Cull: CullMode.Back));
```

| Parameter (default) | Purpose |
|---|---|
| `Shader` | Logical shader name; the backend resolves bytecode + reflection. |
| `VertexEntry` (`"vertexMain"`) / `FragmentEntry` (`"fragmentMain"`) | Entry-point names. For a compute technique the single entry point sits in `FragmentEntry`. |
| `HasVertexInput` (`false`) | Reads a vertex stream (a mesh) vs fabricating a fullscreen triangle. |
| `Blend` (`BlendMode.None`) | Blend equation — pairs with a `.Blends` pass. |
| `LinearFilter` (`false`) | Sample inputs with linear vs nearest filtering. |
| `HasFragmentPush` (`false`) | The fragment/compute stage reads a small inline scalar block (up to sixteen floats). |
| `IsCompute` (`false`) | A compute technique — one entry point, dispatched not drawn. |
| `Address` (`SamplerAddress.Clamp`) | Sampler wrap mode for out-of-range coordinates. |
| `Layout` (`null`) | The per-vertex `VertexLayout` (defaults to `MeshStandard` when `HasVertexInput`). |
| `Instance` (`null`) | The per-instance `VertexLayout` (e.g. `Transform`) for instanced draws. |
| `HasListPush` (`false`) | A draw-list technique's all-stage push block (whose last slot each command's `Tag` rewrites). |
| `Cull` (`CullMode.None`) | Which faces the rasterizer discards — front faces are counter-clockwise. |

Shaders are authored in **Slang** and compiled offline to SPIR-V (Vulkan) + signed DXIL (DirectX 12) with reflection JSON.

### Pipeline-intent enums

| Enum (`namespace`) | Members |
|---|---|
| `BlendMode` (`Loom.Core.Graph`) | `None`, `AlphaOver`, `Additive`, `PremultipliedAlpha`, `Multiply`. |
| `CullMode` (`Loom.Core.Graph`) | `None`, `Back`, `Front` (front faces are counter-clockwise). |
| `SamplerAddress` (`Loom.Core.Materials`) | `Clamp`, `Repeat`, `MirrorRepeat`, `ClampToBorder`, `MirrorClampToEdge`. |

## Drawing, instancing, skinning (`Loom.Core.Hosting`)

`ctx.Draw(in DrawCall)` is the unified raster draw verb. `DrawCall` is a `ref struct`; build the common case with `DrawCall.Create(mesh, technique)`, or use an object initializer for richer calls.

| Member | Role |
|---|---|
| `DrawCall.Create(mesh, technique)` | The terse single-mesh, no-material case. |
| `Technique` (required `string`) | The technique that selects the pipeline. |
| `Mesh` (`Mesh?`) | The mesh to draw; `null` for procedural draws (set `VertexCount`). |
| `SkinnedVertices` (`BufferHandle?`) | A pre-skinned vertex buffer override (the mesh still supplies its indices); must be a `.ReadsVertex` buffer. |
| `VertexCount` (`int`) | Vertices per instance for procedural draws (when `Mesh` is null). |
| `Instances` (`InstanceSource`) | Per-instance data — empty (default) = single draw. |
| `Material` (`Material?`) | Material whose textures fill the technique's sampler slots; `null` for untextured techniques. |
| `Uniforms` / `.SetUniforms<T>(in T)` / `.SetUniforms<T>(ReadOnlySpan<T>)` | The per-draw uniform block (raw bytes matching the shader's cbuffer); set it from a typed value or span. |
| `Sampled` (`ReadOnlySpan<ResourceHandle>`) | Graph resources bound as sampled inputs (each declared via `.Samples`). |
| `DrawCall.From<T>(ReadOnlySpan<T>)` | Build an `InstanceSource` from a CPU instance span (uploaded per frame). |
| `DrawCall.Gpu(BufferHandle, count)` | Build an `InstanceSource` from a GPU-resident buffer a compute pass filled. |

`InstanceSource` (a nested `ref struct`) exposes `IsEmpty` and `Count`; construct it only via `DrawCall.From` / `DrawCall.Gpu`. Skinning is `ctx.DispatchSkin(...)` (a compute pre-skin into a `.ReadsVertex` buffer) followed by an ordinary `ctx.Draw` of that buffer.

## Geometry & materials

| Type (`namespace`) | Role |
|---|---|
| `Mesh` (`Loom.Core.Geometry`) | An opaque handle to uploaded geometry from `IEngine.CreateMesh` / `CreateSkinnedMesh`. Survives graph swaps. |
| `Vertex` (`Loom.Core.Geometry`) | The canonical interleaved vertex — `record struct (Vector3 Position, Vector3 Normal, Vector2 Uv, Vector4 Tangent)`, 48 bytes. |
| `SkinnedVertex` (`Loom.Core.Animation`) | The skinned bind-pose vertex — `Vertex` fields plus four bone indices (`Joint0..3`) + `Vector4 Weights`, 80 bytes. |
| `OverlayVertex` (`Loom.Core.Geometry`) | The 2D overlay vertex (position, uv, premultiplied RGBA16F colour, custom) used by draw lists. |
| `VertexLayout` (`Loom.Core.Geometry`) | A backend-neutral per-stream attribute layout. Built-ins: `MeshStandard`, `MeshSkinned`, `Overlay`, `Transform` (the standard per-instance 4×4 matrix stream). |
| `Material` (`Loom.Core.Materials`) | An opaque `record struct (int Id)` — an ordered `Texture` bundle from `IEngine.CreateMaterial` that fills a technique's reflected sampler slots. |
| `Texture` (`Loom.Core.Materials`) | An opaque handle to an uploaded texture from `IEngine.CreateTexture`. |
| `TextureData` (`Loom.Core.Materials`) | A neutral image description (pixel data + mip chain). Built via `FromRgba8`, `FromRgba16F`, `FromCubeFaces`; carries `Format` (`TextureFormat`), `Width`, `Height`, `Levels`, `IsCube`. |
| `TextureFormat` (`Loom.Core.Materials`) | Source-image encoding (distinct from `Format`): `Rgba8Srgb`, `Rgba8Unorm`, `Rgba16F`. |

## Assets (`Loom.Assets`)

Neutral loaders beside `Loom.Core` (no graphics-API reference). They emit `TextureData` / `Vertex` arrays / a `Skeleton` / `AnimationClip`s; the app turns those into engine meshes, textures, and materials.

| Member | Role |
|---|---|
| `ImageLoader.Load(path, TextureFormat format, generateMips=true)` | Decode a PNG/JPG/… file to `TextureData` with a CPU mip chain. |
| `ImageLoader.Decode(Stream, TextureFormat, generateMips=true)` | Decode an encoded image from a stream (e.g. a glTF-embedded image). |
| `ImageLoader.LoadHdr(path)` / `ImageLoader.DecodeHdr(Stream)` | Decode a Radiance HDR / float image to `Rgba16F` `TextureData`. |
| `GltfLoader.Load(path)` | Load a glTF/GLB model → `LoadedModel`. |
| `LoadedModel` | `Primitives` (`IReadOnlyList<ModelPrimitive>`), `Skeleton?`, `Clips` (`IReadOnlyList<AnimationClip>`). |
| `ModelPrimitive` | `Vertices` (`Vertex[]`), `Indices` (`uint[]`), `Material` (`ModelMaterial?`), `SkinnedVertices` (`SkinnedVertex[]?`), `IsSkinned`. |

## Overlay draw lists (`Loom.Core.Hosting`)

The cross-backend path for overlay rendering (game UI via SUIsei, ImGui, debug lines). A producer composites a `DrawListData` per channel and the host hands them in via `FrameInputs.DrawLists`; a pass pulls one out of `ctx.DrawLists` by id and submits it with `ctx.DrawList<TVertex>(...)`.

| Type | Role |
|---|---|
| `DrawListId` | An opaque `record struct (int Value)` — a producer-assigned id per list (the screen overlay and each in-world surface gets a distinct one). |
| `DrawListData` | One frame's flattened list: `Vertices` (`ReadOnlyMemory<OverlayVertex>`), `Indices` (`ReadOnlyMemory<ushort>`), `Commands` (`ReadOnlyMemory<DrawCommand>`), `CoordinateSize`, `IsHdrTarget`, `Revision`; `Dump()` yields a deterministic text snapshot. `DrawListData.Empty` is the no-op. |
| `DrawCommand` | One run within a list — `record struct (Rect ClipRect, int TextureIndex, int IndexOffset, int IndexCount, int VertexOffset, uint Tag)`. `Tag` is the per-command value a list-push technique reads. |
