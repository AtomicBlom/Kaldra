# Loom.UI.SUIsei — SUIsei integration

`Loom.UI.SUIsei` bridges the [SUIsei](https://github.com/AtomicBlom/SUIsei) XAML UI runtime with the [Loom render-graph engine](https://github.com/AtomicBlom/Loom), submitting SUIsei's composited draw lists through Loom's backend-neutral draw-list API.

## What it does

SUIsei is headless: a `UIHost` ticks, routes input, and composites your XAML visual tree into neutral draw lists (vertices + indices + clip/texture/range commands) — it holds no engine or backend handle and never touches the GPU. This package is the seam that gets those draw lists onto the screen:

- Provides `UIPainter` — submit a host's composited draw lists into a render pass through `IRenderContext.DrawList`. SUIsei's draw-list structs are binary-identical to Loom's, so the submission is zero-copy.
- Registers the `ui` technique, and uploads, shares and releases the textures the draw lists sample — the white texel, the glyph atlas pages, images — through the public engine API.
- Includes the UI shader (compiled from Slang) distributed as a content file alongside the package.

Neither the Vulkan nor the DirectX 12 backend references SUIsei — the integration lives entirely in this package, on top of the same neutral draw-list verb the ImGui overlay uses.

## Usage

The headless `UIHost` and the `UIPainter` split cleanly: the host you tick and composite each frame; the painter you create once and keep for the engine's lifetime (a painter is a stable handle bundle, so a pass closure may capture it).

```csharp
// Once: the headless UI host, a root view, and the painter that draws it.
var host = new UIHost();
var view = host.CreateRoot(layer);                 // returns a UIView; view.Surface is its draw-list id
var ui   = UIPainter.Create(engine, host);          // no font: glyph atlas pages arrive with the draw lists

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

Each frame, before `engine.Render(...)`, advance the host headlessly and prepare the painter:

```csharp
host.Tick(deltaSeconds, time, framebufferSize);   // drain input → hit-test → route → lay out
host.Render();                                     // composite each view → host.DrawLists
ui.Prepare();                                      // upload, share and release what the lists sample
```

`ui.Draw(ctx, view.Surface)` then submits that surface's composited list, binding what `Prepare` resolved; it is a no-op when the frame carries no list for that id or the list is empty.

**Textures come in the draw list.** Each list carries its own slot table (`DrawListData.TextureSlots`): slot 0 is the white texel, then the glyph-atlas pages and images its commands sample, each once, and a command's `TextureIndex` indexes that table. The painter keeps one texture per source, shared by every surface that names it, so an image shown in two places uploads once. It rebuilds a surface's slot→texture array only when the list's `TextureRevision` moves, and when no surface names a source any more — the image left the UI, or its root was removed — it releases the texture with `engine.ReleaseTexture`, which keeps it until the frames in flight are done with it. A source whose content `Version` moves is uploaded again and the old texture released. There is no font to pass: text samples the compositor's own atlas pages, so UVs always match.

**Why `Prepare` is a separate step.** `Draw` runs inside a pass callback, while the engine is recording the frame. A texture created there would be uploaded only ahead of the *next* frame, so the first frame to show a new image would sample nothing — the engine therefore refuses to create or release a texture inside a pass. `Prepare` does that work between frames instead, and only when a surface's list has changed, so a steady UI costs no allocation. `Draw` throws if `Prepare` did not run for the frame it is drawing (or ran before the host last rendered), naming the missing step, rather than drawing a frame that is only right while the UI happens not to change.

**Edges antialias over one pixel at any angle.** Rounded rects, borders and vector shapes turn a signed distance into coverage by dividing it by the length of its screen-space gradient, so an edge ramps across one pixel whether it runs straight or diagonally, and under any transform. A vector shape (kind 7) arrives from SUIsei's tessellator as triangles whose four `Custom` floats are signed distances to the edges each one antialiases. SUIsei's software rasterizer computes the same coverage, and `Loom.Tests`' `VectorPrimRenderTests` holds the GPU to it.

**Unknown command kinds fail loud.** A draw command's tag selects the `ui` shader's branch: kinds 0–4 (fill, rounded rect, border, glyph, image) and 7 (vector) are drawn, and 5 and 6 are group-opacity layer markers. A shader cannot reject a value it does not know — an unknown kind would fall through to a plain fill — so `Prepare` checks every command of a changed list and throws, naming the command and surface, on a kind outside that set — or on a `TextureIndex` past the end of the list's slot table. The checks run once per revision, not per frame.

**The draw contract is guarded.** The painter hands SUIsei's `OverlayVertex` and `DrawCommand` spans to the backend as Loom's types with a zero-copy cast, which checks nothing. `Loom.Tests`' `DrawContractLayoutTests` writes a distinct value into every field on the SUIsei side, reads it back through Loom's type, and fails if a size, offset or field type has drifted on either side.

**Several hosts in one frame.** Every `UIHost` numbers its surfaces from zero, so a HUD and an `InWorldPanel` both have a surface 0. The painter does not use those ids for the geometry the backend keeps between frames: it mints a draw-list stream per surface from `engine.AllocateDrawListStream()`, unique on the engine, so any number of hosts and painters can draw in the same frame.

`InWorldPanel` drives its own host and painter: its `Render()` composites and prepares in one call, so call it each frame before `engine.Render(...)` and draw it with `panel.Draw(ctx)`.

## Dependencies

- [`Loom`](https://www.nuget.org/packages/Loom) — describe layer
- [`SUIsei`](https://www.nuget.org/packages/SUIsei) — UI runtime (transitive)

Reference [`Loom.Sdk`](https://www.nuget.org/packages/Loom.Sdk) with `<UseSUIsei>true</UseSUIsei>` to pull this package in automatically alongside the selected backend.
