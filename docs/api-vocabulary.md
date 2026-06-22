# Loom API Vocabulary

Quick reference for every key type in the public API.

## Graph description (`Loom.Core`)

| Type | Role |
|---|---|
| `RenderGraph` | Immutable, hashable description of your full render graph. Produced by `RenderGraph.Define`; passed to `IEngine.SetGraph`. |
| `GraphBuilder` | Fluent root builder (`g => { … }`). Declares domains, resources, and passes. |
| `Domain` | A named resolution domain. Fixed (`g.Domain(name, w, h)`) or backbuffer-relative (`g.Domain(name, scale)`). `g.Screen` is the backbuffer-size domain. |
| `ColorHandle` | Typed handle for a colour texture resource. Returned by `g.Color(name, format, domain)`. |
| `DepthHandle` | Typed handle for a depth/stencil texture. Returned by `g.Depth(name, format, domain)`. |
| `BufferHandle` | Typed handle for a GPU buffer (structured storage). Returned by `g.Buffer(name, stride, count)`. |
| `PassBuilder` | One GPU pass inside `g.Pass(name, pb => { … })`. Declares resource accesses and a draw/dispatch callback. |

### Pass access declarations

| Method | Semantics |
|---|---|
| `.Writes(handle)` | Produce a colour target. The engine may clear it first (`LoadOp.Clear`). |
| `.Blends(handle)` | Accumulate onto an existing colour target (`LoadOp.Load`). The pass is both the last reader and the next writer. |
| `.WritesDepth(handle)` | Produce a depth target (`LoadOp.Clear`). |
| `.ReadsDepth(handle)` | Sample the depth buffer read-only (depth test without write). |
| `.Samples(handle)` | Declare a shader-read dependency on a texture or buffer. The engine inserts the read barrier / layout transition. |
| `.ReadWrites(handle)` | Read and write a buffer in the same pass (compute UAV). |
| `.ReadsVertex(handle)` | Bind a buffer as a vertex/index input for a draw pass. |
| `.Present()` | Mark this pass's write as the final swapchain present. |
| `.Draw(callback)` | Supply the per-frame draw callback (receives `IRenderContext`). |
| `.Dispatch(callback)` | Supply the per-frame dispatch callback (receives `IRenderContext`). |

### Special handles

| Property | Description |
|---|---|
| `g.Backbuffer` | The swapchain colour target (`ColorHandle`). A pass that `.Writes(g.Backbuffer).Present()` presents that frame. |
| `g.Backbuffer.Domain` | The native-resolution `Domain` (same as `g.Screen`). |

## Engine hosting (`Loom.Core.Hosting`)

| Type | Role |
|---|---|
| `IEngine` | The live rendering engine. Create via `Engine.AvailableEngines["Vulkan"].Create(settings)`. Owns the device and pipeline cache. Survives graph swaps (`SetGraph`). |
| `Engine` | Static registry of available backends (`Engine.AvailableEngines`). Populated by each backend assembly's `[ModuleInitializer]`. |
| `IRenderContext` | Per-frame execution context passed to `.Draw` / `.Dispatch` closures. Closures *pull* from it rather than capturing, keeping the graph description frame-independent. |
| `EngineSettings` | Backend-neutral engine settings: `HdrOutput`, `UseNamedGPUObjects`, `WatchShaderDirectory`, etc. |
| `FrameInputs` | Per-frame host data supplied to `IEngine.Render`: `Camera`, `SunView`, `DeltaTime`, `HoverId`, overlay draw lists. |
| `TechniqueDescriptor` | Shader technique definition: vertex shader name, fragment shader name, blend mode, depth state. Registered via `IEngine.DefineTechnique`. |

### `IRenderContext` members

| Member | Description |
|---|---|
| `ctx.Camera` | The primary view + projection camera. |
| `ctx.SunView` | The sun's view + projection (single shadow map). |
| `ctx.Cascades` | Sun views for cascaded shadow maps (nearest first). |
| `ctx.CascadeSplits` | Per-cascade far distances for the lighting pass to select a cascade. |
| `ctx.Time` | Elapsed seconds since startup. |
| `ctx.DeltaTime` | Seconds since the previous frame. |
| `ctx.HoverId` | Object ID under the cursor (0 = nothing). |
| `ctx.HdrOutput` | `true` when the engine is in HDR output mode. |
| `ctx.FullScreen(technique, inputs...)` | Draw a fullscreen triangle with a named technique. |
| `ctx.Draw(mesh, technique)` | Draw a mesh with a named technique. |
| `ctx.Draw(mesh, material, technique)` | Draw a mesh with material texture binding. |
| `ctx.DrawInstanced(mesh, technique, instances)` | GPU-instanced draw. |
| `ctx.Dispatch(technique, x, y, z)` | Compute dispatch. |
| `ctx.DrawList(id)` | Acquire an overlay draw list for this frame. |

## Geometry and materials (`Loom.Core`)

| Type | Role |
|---|---|
| `Mesh` | Opaque handle to uploaded geometry. Created by `IEngine.CreateMesh`. Survives graph swaps. |
| `Vertex` | Standard mesh vertex: position, normal, UV, tangent. |
| `SkinnedVertex` | Skinned mesh vertex: standard fields + bone indices + bone weights. |
| `VertexLayout` | Describes the per-vertex attribute layout for a custom vertex type. `VertexLayout.MeshStandard` and `VertexLayout.MeshSkinned` are the built-ins. |
| `Material` | A bag of `Texture` handles that fills a technique's sampler slots at draw time. |
| `Texture` | Opaque handle to an uploaded texture. Created by `IEngine.CreateTexture`. |
| `TextureData` | Neutral image description: pixel data + mip chain. Built by `Loom.Assets` from decoded images. |

## Assets (`Loom.Assets`)

| Type | Role |
|---|---|
| `ImageLoader` | Decodes image files (PNG, JPEG, …) to `TextureData` with an auto-generated CPU mip chain. |
| `GltfLoader` | Loads glTF 2.0 models into `Mesh` + `Material` + `Skeleton` + `AnimationClip` neutral descriptions. |

## Overlay draw lists

Draw lists are the cross-backend path for overlay rendering (UI, debug lines, ImGui). A pass declares no render state for them; they're submitted via `ctx.DrawList(id)` and consumed by a dedicated drawing pass.

| Type | Role |
|---|---|
| `DrawListId` | Opaque identifier for a named draw-list channel. |
| `DrawListData` | Mutable per-frame accumulator for a draw list: geometry, vertices, indices. |
| `DrawCommand` | One draw call inside a `DrawListData`: vertex/index offset + count + technique. |
