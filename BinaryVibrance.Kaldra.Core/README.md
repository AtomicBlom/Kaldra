# Kaldra — describe layer

`BinaryVibrance.Kaldra.Core` is the authoring API for the [Kaldra render-graph engine](https://github.com/AtomicBlom/Kaldra). You declare *what* each render pass reads and writes; the engine derives ordering, barriers, image-layout transitions, transient memory aliasing, and CPU↔GPU synchronisation — no manual barrier calls.

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

`BinaryVibrance.Kaldra.Core` is the **describe** layer — it allocates freely and contains no graphics-API types. The **execute** layer ([`BinaryVibrance.Kaldra.Graph`](https://www.nuget.org/packages/BinaryVibrance.Kaldra.Graph)) derives the frame plan; the backends ([`BinaryVibrance.Kaldra.Backend.Vulkan`](https://www.nuget.org/packages/BinaryVibrance.Kaldra.Backend.Vulkan) / [`BinaryVibrance.Kaldra.Backend.DirectX12`](https://www.nuget.org/packages/BinaryVibrance.Kaldra.Backend.DirectX12)) record real GPU work.

Reference [`BinaryVibrance.Kaldra.Sdk`](https://www.nuget.org/packages/BinaryVibrance.Kaldra.Sdk) from a game project to pull in the engine automatically.
