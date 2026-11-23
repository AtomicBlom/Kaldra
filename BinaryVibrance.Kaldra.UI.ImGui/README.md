# BinaryVibrance.Kaldra.UI.ImGui — Dear ImGui overlay

`BinaryVibrance.Kaldra.UI.ImGui` adds an optional [Dear ImGui](https://github.com/ocornut/imgui) debug overlay to the [Kaldra render-graph engine](https://github.com/AtomicBlom/Kaldra) via the backend-neutral draw-list API — neither `BinaryVibrance.Kaldra.Core` nor either backend references ImGui.NET directly.

## Design

ImGui draw data is routed through `IRenderContext.DrawList`, the same path Vysara and any other overlay use. An ImGui draw list is the same shape as any other (vertices + 16-bit indices + clip/texture/range commands), so the backends need no ImGui-specific renderer. This means:

- **Opt-in** — reference this package to add the overlay; omit it and the engine is wholly ImGui-free.
- **Backend-neutral** — works with both `BinaryVibrance.Kaldra.Backend.Vulkan` and `BinaryVibrance.Kaldra.Backend.DirectX12` without changes.
- Includes the UI shader (compiled from Slang) distributed as a content file alongside the package.

## Usage

The bridge is `ImGuiPainter`. Create it once after the engine and the ImGui context exist — `Create` registers the `imgui` technique and uploads the current context's font atlas through the public engine API — then keep it for the engine's lifetime. A painter is a stable handle bundle, so a pass closure may capture it without breaking the frame-independent-graph rule.

```csharp
// Once, after the engine and the ImGui context are set up (fonts added, style set):
var hud = ImGuiPainter.Create(engine);

var graph = RenderGraph.Define(g =>
{
    // … your scene passes write g.Backbuffer …

    // The HUD blends on top of the finished frame:
    g.Pass("hud")
        .Blends(g.Backbuffer)
        .Draw(ctx => hud.Draw(ctx));

    g.Present(g.Backbuffer);
});
engine.SetGraph(graph);
```

Each frame, drive ImGui as usual on the host (`ImGui.NewFrame()` → widgets → `ImGui.Render()`) **before** `engine.Render(...)`; `hud.Draw(ctx)` submits the draw data `ImGui.Render()` produced. It is a no-op when there is nothing to draw.

`ImGuiPainter` also carries the engine's two debug overlays, refreshed per frame:

- `SetThumbnails(resourceIds)` — preview graph resources (`IEngine.InspectableResources` ids) as a thumbnail grid: the resource / G-buffer channel viewer.
- `SetDebugLines(segments)` — overlay line segments (the detached-camera frustum-freeze wireframe).

## Dependencies

- [`Kaldra`](https://www.nuget.org/packages/Kaldra) — describe layer
- [`ImGui.NET`](https://www.nuget.org/packages/ImGui.NET) — Dear ImGui bindings (transitive)

Reference [`BinaryVibrance.Kaldra.Sdk`](https://www.nuget.org/packages/BinaryVibrance.Kaldra.Sdk) with `<UseImGui>true</UseImGui>` to pull this package in automatically.
