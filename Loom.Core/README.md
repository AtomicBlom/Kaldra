# Loom — describe layer

`Loom` is the authoring API for the [Loom render-graph engine](https://github.com/AtomicBlom/Loom). You declare *what* each render pass reads and writes; the engine derives ordering, barriers, image-layout transitions, transient memory aliasing, and CPU↔GPU synchronisation — no manual barrier calls.

## Key types

| Type | Role |
|---|---|
| `GraphBuilder` | Fluent builder — declare passes, resources, and their dependencies |
| `PassBuilder` | Per-pass builder: `.Writes`, `.Samples`, `.Blends`, `.ReadWrites`, `.ReadsVertex` |
| `ColorHandle` / `DepthHandle` / `BufferHandle` | Typed resource handles — the graph tracks edges through these |
| `Domain` | Fixed or backbuffer-relative resolution for a resource |
| `RenderGraph` | The immutable, hashable description produced by `GraphBuilder.Build()` |
| `IEngine` | Backend entry point (`SetGraph`, `DefineTechnique`, `Render`) |

## Minimal example

```csharp
var graph = new GraphBuilder()
    .AddPass("scene", pass => pass
        .Writes(color)
        .WritesDepth(depth)
        .Draw((ctx, _) => ctx.Draw(mesh, "lit")))
    .AddPass("tone", pass => pass
        .Samples(color)
        .Writes(GraphBuilder.Backbuffer)
        .Draw((ctx, _) => ctx.FullScreen("tonemap", color)))
    .Build();

engine.SetGraph(graph);
```

## Architecture

`Loom` is the **describe** layer — it allocates freely and contains no graphics-API types. The **execute** layer ([`Loom.Graph`](https://www.nuget.org/packages/Loom.Graph)) derives the frame plan; the backends ([`Loom.Backend.Vulkan`](https://www.nuget.org/packages/Loom.Backend.Vulkan) / [`Loom.Backend.DirectX12`](https://www.nuget.org/packages/Loom.Backend.DirectX12)) record real GPU work.

Reference [`Loom.Sdk`](https://www.nuget.org/packages/Loom.Sdk) from a game project to pull in the engine automatically.
