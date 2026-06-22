# Loom Architecture

Loom is a **declarative, render-graph-driven rendering engine for .NET**. You describe *what* each pass reads and writes; the engine derives execution order, GPU barriers, image-layout transitions, transient-resource aliasing, and CPU↔GPU synchronisation.

## Three layers

```
Loom.Core  ──(Describe)──►  Loom.Graph  ──(Execute)──►  Loom.Vulkan
(your code)                                              Loom.DirectX12
```

### Describe — `Loom.Core`

Immutable, hashable, fluent builders. You call `RenderGraph.Define` once per structural change and get back an immutable `RenderGraph` value that encodes your full intent:

- `GraphBuilder` — the root builder (`g => { … }`)
- `Domain` — a named resolution domain (fixed size or backbuffer-relative)
- `ColorHandle / DepthHandle / BufferHandle` — typed resource handles
- `PassBuilder` — one GPU pass; declares `.Writes`, `.Blends`, `.Samples`, `.WritesDepth`, `.ReadsDepth`, `.ReadsVertex`, `.ReadWrites`
- Pass callbacks (`.Draw`, `.Dispatch`) — closures that pull per-frame data from `IRenderContext`

No graphics-API type appears here. This layer allocates freely and is safe to call from any thread.

### Execute — `Loom.Graph`

Per-frame: topological sort from the read/write edges, barrier/layout-transition derivation, transient-resource aliasing, lifetime analysis. Produces backend-neutral scheduling decisions. Both backends consume the same schedule.

`EngineBase` owns the shared engine lifecycle: technique registration, shader hot-reload, GPU-timing decode, and frame capture. Backend-specific execution (command recording) stays in each backend.

### Bake + Record — `Loom.Vulkan` / `Loom.DirectX12`

Translate the neutral schedule into real GPU work. Backends:

- Create pipelines and descriptor layouts from the Slang reflection JSON (cached by hash for the engine's lifetime)
- Emit `vkCmdPipelineBarrier2` (Vulkan 1.3) or D3D12 resource transitions
- Call into your pass `.Draw` / `.Dispatch` closures to record draw calls

No graphics-API type leaks into `Loom.Core` or `Loom.Graph`. Introducing one is the cardinal architectural violation.

## Dependency direction

```
Loom.Sandbox ──► Loom.Vulkan ──► Loom.Graph ──► Loom.Core
             └──► Loom.DirectX12 ──►┘
```

`Loom.Assets` sits beside `Loom.Core` — both are backend-neutral and have no graphics-API references.

## Shader pipeline

Shaders are written in **Slang** and compiled offline to SPIR-V (Vulkan) and signed DXIL (DX12) by the `Loom.Sdk` MSBuild targets at build time. Reflection JSON is emitted alongside each compiled shader; the backend reads it at `DefineTechnique` time to derive descriptor-binding layouts without you specifying them.

Engine shaders (blit, tonemap, auto-exposure) are **embedded** in the backend assemblies. Your app's shaders compile to **loose files** (`.spv`/`.dxil`/`.json`) copied to `shaders/` in the output directory.

## Backend selection

Backends self-register via `[ModuleInitializer]` into `Engine.AvailableEngines`. Including a backend assembly in your project is enough to make it available; which one runs is a runtime decision passed to `Engine.AvailableEngines[name].Create(settings)`.

When using `Loom.Sdk` (NuGet or submodule), the `UseVulkan` / `UseDirectX12` MSBuild properties control which backend assemblies are referenced and therefore which `[ModuleInitializer]` calls fire.
