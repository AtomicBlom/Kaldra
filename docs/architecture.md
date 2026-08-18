# Loom Architecture

Loom is a **declarative, render-graph-driven rendering engine for .NET**. You describe *what* each pass reads and writes; the engine derives execution order, GPU barriers, image-layout transitions, transient-resource aliasing, and CPU↔GPU synchronisation.

## Three layers

```
Loom.Core  ──(Describe)──►  Loom.Graph  ──(Execute)──►  Loom.Backend.Vulkan
(your code)                                              Loom.Backend.DirectX12
```

### Describe — `Loom.Core`

Immutable, hashable, fluent builders. You call `RenderGraph.Define` once per structural change and get back an immutable `RenderGraph` value that encodes your full intent:

- `GraphBuilder` — the root builder (`g => { … }`)
- `Domain` — a named resolution domain (fixed size or backbuffer-relative)
- `ColorHandle / DepthHandle / BufferHandle` — typed resource handles
- `PassBuilder` — one GPU pass; declares `.Writes`, `.Blends`, `.Samples`, `.WritesDepth`, `.ReadsDepth`, `.ReadsVertex`, `.ReadWrites`, `.Reads`
- `ConstantsHandle` / `StorageBufferHandle` — neutral per-frame channels the app fills by handle (`IEngine.Write`) and a pass `.Reads`; how view/light/time data reaches a pass without the engine naming a game concept
- Pass callbacks (`.Draw`, `.Dispatch`) — closures that pull per-frame data from `IRenderContext` and from the `Constants`/`Storage` buffers the pass reads, rather than capturing it

No graphics-API type appears here. This layer allocates freely and is safe to call from any thread.

### Execute — `Loom.Graph`

Per-frame: topological sort from the read/write edges, barrier/layout-transition derivation, transient-resource aliasing, lifetime analysis. Produces backend-neutral scheduling decisions. Both backends consume the same schedule.

`EngineBase` owns the shared engine lifecycle: technique registration, shader hot-reload, GPU-timing decode, and frame capture. Backend-specific execution (command recording) stays in each backend.

### Bake + Record — `Loom.Backend.Vulkan` / `Loom.Backend.DirectX12`

Translate the neutral schedule into real GPU work. Backends:

- Create pipelines and descriptor layouts from the Slang reflection JSON (cached by hash for the engine's lifetime)
- Emit `vkCmdPipelineBarrier2` (Vulkan 1.3) or D3D12 resource transitions
- Call into your pass `.Draw` / `.Dispatch` closures to record draw calls

No graphics-API type leaks into `Loom.Core` or `Loom.Graph`. Introducing one is the cardinal architectural violation.

## Dependency direction

```
                       ┌──► Loom.Backend.Vulkan    ─┐
your app ──► host ─────┼──► Loom.Backend.DirectX12 ─┼──► Loom.Graph ──► Loom.Core
             │         └──► Loom.Backend.WebGPU    ─┘
             │
             └── Loom.Hosting.Desktop (window, surface, frame loop, CLI flags)
                 Loom.Hosting.Web     (browser, paired with the WebGPU backend)
```

Beside the engine tiers, all backend-neutral and free of graphics-API references:

| Project | Role |
|---|---|
| `Loom.Assets` | image + glTF loaders, and the material upload bridge |
| `Loom.Concepts` | app-side helpers — `Camera`, `Frustum`, `Scene`. Deliberately **not** engine API |
| `Loom.Input` | actions, bindings, layered event routing |
| `Loom.UI.SUIsei` / `Loom.UI.ImGui` | UI bridges; both reach the GPU only through the neutral draw-list verb, so no backend references a UI library |

## Shader pipeline

Shaders are written in **Slang** and compiled offline to SPIR-V (Vulkan), signed DXIL (DirectX 12), and WGSL (WebGPU) by the `Loom.Sdk` MSBuild targets at build time. Reflection JSON is emitted alongside each compiled shader; the backend reads it at `DefineTechnique` time to derive descriptor-binding layouts without you specifying them. The Slang-side contract — the platform macros and the binding rules — is [`shader-authoring.md`](shader-authoring.md).

Engine shaders (blit, tonemap, auto-exposure) are **embedded** in the backend assemblies. Your app's shaders compile to **loose files** (`.spv`/`.dxil`/`.json`) copied to `shaders/` in the output directory.

## Backend selection

Backends self-register via `[ModuleInitializer]` into `Backends.Available` (an `IReadOnlyList<IRenderBackend>`). Including a backend assembly in your project is enough to make it available; which one runs is a runtime decision — the host picks a registered backend by name (`Backends.Available.FirstOrDefault(b => b.Name == settings.PreferredBackend)`) and calls `backend.Create(surface, width, height, settings)`, handing over the window's native surface (Vulkan `IVkSurface` / DirectX 12 `HWND`).

When using `Loom.Sdk` (NuGet or submodule), the `UseVulkan` / `UseDirectX12` / `UseWebGPU` MSBuild properties control which backend assemblies are referenced and therefore which `[ModuleInitializer]` calls fire; `UseSUIsei` / `UseImGui` do the same for the UI bridges.

## Threading: one thread records

**Everything that touches the recording path happens on one thread — the one that created the engine.** That means `Render`, `SetGraph`, and the whole `Write` family (`Write(Material, …)`, `Write(ConstantsHandle, …)`, `Write(StorageBufferHandle, …)`). This is not a lock you can take; nothing in that path is externally synchronized. A backend's command pool and queue are not synchronized objects in Vulkan or DirectX 12, and the engine's per-frame shadow buffers are plain dictionaries. A second thread does not contend — it corrupts, usually in a way that surfaces far from the call responsible.

Debug builds enforce it: `EngineBase.AssertRenderThread` compares the calling thread against the one that ran the constructor and throws naming both. It is `[Conditional("DEBUG")]` because it is a contract violation to fix in development, not a cost to carry in a shipped frame loop.

The deliberate exception is **asset creation**. `CreateTextureAsync`, `CreateMeshAsync` and their siblings may be called from any thread — a background image decode, a demo preloading concurrently — because they queue the GPU work rather than doing it. `ServicePendingUploads` drains that queue on the render thread. That single seam exists precisely because the rest of the surface has this affinity: without it, every background loader would need to marshal its own results.

This is worth knowing before refactoring a backend toward parallel command recording. The contract is not an accident of the current implementation — the ordering it buys is load-bearing. `Write` provably runs before `Render` and outside command recording, which is what lets a growable storage buffer reallocate in place (see `EngineBase.GrowStorageBuffer`): there is no command buffer open to invalidate.

## What a new backend has to write

Most of a backend is not API code. The per-frame sequence, the derived plan's interpretation, and the draw-list state machine are the same everywhere, so they live once — in `EngineBase` and in `Loom.Core.Hosting` — and a backend inherits them.

`EngineBase.RecordFrame` owns the frame's shape: flush the app-writable buffer shadows, then per pass its debug-label scope, its derived transitions, its timestamps and its own recording, then close the trailing labels, copy the readback targets, and finish the frame. The order is the point. Each step has to happen in that sequence on every backend — a pass's barriers before its opening timestamp, because those barriers are the cost of the transition rather than of the pass; the readback copies before the frame's tail, because the tail may transition the very targets being copied — and a transliterated sequence is a sequence that can drift. It had: two backends recorded readback then capture and the third recorded capture then readback.

So a new backend implements hooks, not a loop:

| Hook | What it does |
|---|---|
| `WriteConstantsShadow` / `WriteStorageShadow` | Upload one app-writable buffer's bytes for this frame |
| `EmitTransitions` | Translate the derived `ResourceTransition`s (an API that tracks state itself overrides this as an explicit no-op) |
| `RecordComputePass` / `RecordRasterPass` | Open a scope over the pass's derived attachments and run its callback |
| `RecordReadback` | Copy each `g.Readback` target into this slot's buffer |
| `FinishFrame` | The capture, if requested, and the derived final transitions |
| `BeginLabel` / `EndLabel` | Open and close a named scope for a capture tool (or empty, deliberately) |
| `BeginFrameTimestamps` / `WritePassBeginTimestamp` / `WritePassEndTimestamp` / `EndFrameTimestamps` | Per-pass GPU timing (virtual — a backend with no timer implements none) |

`RecordRasterPass` stays whole to the backend rather than being templated further. Its attachment descriptors, formats and views are almost entirely API structs, and forcing them into one signature is exactly what would push graphics types into `Loom.Graph`. What it inherits instead is the loop around it, the label bookkeeping, the transition emission, the timing index arithmetic, and `GraphPass.ResourceIdsWith` for the buffers a pass reads.

Draw lists are handled the same way. `DrawListWalker` decodes a submission's `DrawCommand` stream into steps — `Draw`, `BeginLayer`, `EndLayer` — and owns the group-opacity nesting, the nesting cap and its symmetric suppression, the scissor, the fully-clipped skip, and the per-command texture memoisation. `DrawListGeometry` answers the geometry: a layer's bounds, its target-pixel area, the composite quad, and the marker tag values the producer encodes. A backend's `DrawList` is then a `while (walker.MoveNext())` over three cases, each one just the API calls that step needs. A layer's identity is its **depth** — every backend's layer pool is indexed by depth already, so none of them keeps a stack of its own.

What is deliberately *not* shared is `*RenderContext.Verbs.cs`. Those files are ~90% descriptor-set, root-signature and bind-group construction; the only neutral part is a small geometry-source switch, which is not worth an abstraction. Policy differences between backends' record paths belong in `RecordValidator`, which exists for exactly that and is already one implementation.

The neutral floors a backend can rely on rather than re-check are in `GraphLimits` — uniform and storage reads per pass, colour attachments per pass — and derivation rejects a graph that exceeds them before any frame is recorded. `EngineCapabilities` reports the headroom a particular device has above those floors.
