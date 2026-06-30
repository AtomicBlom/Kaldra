# Loom.UI.SUIsei — SUIsei integration

`Loom.UI.SUIsei` bridges the [SUIsei](https://github.com/AtomicBlom/SUIsei) XAML UI runtime with the [Loom render-graph engine](https://github.com/AtomicBlom/Loom), submitting SUIsei's composited draw lists through Loom's backend-neutral draw-list API.

## What it does

SUIsei is headless: a `UIHost` ticks, routes input, and composites your XAML visual tree into neutral draw lists (vertices + indices + clip/texture/range commands) — it holds no engine or backend handle and never touches the GPU. This package is the seam that gets those draw lists onto the screen:

- Provides `UIPainter` — submit a host's composited draw lists into a render pass through `IRenderContext.DrawList`. SUIsei's draw-list structs are binary-identical to Loom's, so the submission is zero-copy.
- Registers the `ui` technique and uploads the UI textures (a white texel for fills/borders, plus the optional MTSDF glyph atlas) through the public engine API.
- Includes the UI shader (compiled from Slang) distributed as a content file alongside the package.

Neither the Vulkan nor the DirectX 12 backend references SUIsei — the integration lives entirely in this package, on top of the same neutral draw-list verb the ImGui overlay uses.

## Usage

The headless `UIHost` and the `UIPainter` split cleanly: the host you tick and composite each frame; the painter you create once and keep for the engine's lifetime (a painter is a stable handle bundle, so a pass closure may capture it).

```csharp
// Once: the headless UI host, a root view, and the painter that draws it.
var host = new UIHost();
var view = host.CreateRoot(layer);                 // returns a UIView; view.Surface is its draw-list id
var ui   = UIPainter.Create(engine, host, fontAtlas);   // pass null for rect/border-only surfaces (no text)

var graph = RenderGraph.Define(g =>
{
    // … your scene passes write g.Backbuffer …

    g.Pass("ui")
        .Blends(g.Backbuffer)
        .Draw(ctx => ui.Draw(ctx, view.Surface));

    g.Present(g.Backbuffer);
});
engine.SetGraph(graph);
```

Each frame, before `engine.Render(...)`, advance the host headlessly:

```csharp
host.Tick(deltaSeconds, time, framebufferSize);   // drain input → hit-test → route → lay out
host.Render();                                     // composite each view → host.DrawLists
```

`ui.Draw(ctx, view.Surface)` then submits that surface's composited list; it is a no-op when the frame carries no list for that id or the list is empty.

## Dependencies

- [`Loom`](https://www.nuget.org/packages/Loom) — describe layer
- [`SUIsei`](https://www.nuget.org/packages/SUIsei) — UI runtime (transitive)

Reference [`Loom.Sdk`](https://www.nuget.org/packages/Loom.Sdk) with `<UseSUIsei>true</UseSUIsei>` to pull this package in automatically alongside the selected backend.
