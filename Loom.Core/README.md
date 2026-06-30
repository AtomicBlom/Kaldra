# Loom — describe layer

`Loom` is the authoring API for the [Loom render-graph engine](https://github.com/AtomicBlom/Loom). You declare *what* each render pass reads and writes; the engine derives ordering, barriers, image-layout transitions, transient memory aliasing, and CPU↔GPU synchronisation — no manual barrier calls.

## Key types

| Type | Role |
|---|---|
| `RenderGraph` | The immutable, hashable graph description; build one with `RenderGraph.Define(g => …)` |
| `GraphBuilder` | The `g` inside `RenderGraph.Define` — declares passes, resources, and domains |
| `PassBuilder` | Per-pass builder: `.Writes`, `.Samples`, `.Blends`, `.WritesDepth`, `.ReadWrites`, `.ReadsVertex`, `.Reads` |
| `ColorHandle` / `DepthHandle` / `BufferHandle` | Typed resource handles — the graph tracks edges through these |
| `Domain` | Fixed or backbuffer-relative resolution; its `.Color` / `.Depth` verbs declare targets |
| `IEngine` | Backend entry point (`SetGraph`, `DefineTechnique`, `Render`, `Write`) |

## Minimal example

```csharp
var graph = RenderGraph.Define(g =>
{
    var color = g.Screen.Color("color", Format.Rgba16F);   // an HDR scene target
    var depth = g.Screen.Depth("depth", Format.Depth32F);

    g.Pass("scene")                                         // declare reads/writes — ordering & barriers are derived
        .Writes(color)
        .WritesDepth(depth)
        .Draw(ctx => ctx.Draw(DrawCall.Create(mesh, "lit")));

    g.Pass("tonemap")
        .Samples(color)                                     // read the HDR target …
        .Writes(g.Backbuffer)                               // … resolve it to the swapchain
        .Draw(ctx => ctx.FullScreen("tonemap", color));

    g.Present(g.Backbuffer);
});

engine.SetGraph(graph);
```

## Architecture

`Loom` is the **describe** layer — it allocates freely and contains no graphics-API types. The **execute** layer ([`Loom.Graph`](https://www.nuget.org/packages/Loom.Graph)) derives the frame plan; the backends ([`Loom.Backend.Vulkan`](https://www.nuget.org/packages/Loom.Backend.Vulkan) / [`Loom.Backend.DirectX12`](https://www.nuget.org/packages/Loom.Backend.DirectX12)) record real GPU work.

Reference [`Loom.Sdk`](https://www.nuget.org/packages/Loom.Sdk) from a game project to pull in the engine automatically.
