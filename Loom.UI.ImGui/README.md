# Loom.UI.ImGui — Dear ImGui overlay

`Loom.UI.ImGui` adds an optional [Dear ImGui](https://github.com/ocornut/imgui) debug overlay to the [Loom render-graph engine](https://github.com/AtomicBlom/Loom) via the backend-neutral draw-list API — neither `Loom.Core` nor either backend references ImGui.NET directly.

## Design

ImGui draw data is routed through `IRenderContext.DrawList`, the same path as SUIsei and any other overlay. This means:

- **Opt-in**: reference this package to add the overlay; omit it and the engine is wholly ImGui-free
- **Backend-neutral**: works with both `Loom.Backend.Vulkan` and `Loom.Backend.DirectX12` without changes
- Includes UI shaders distributed as content files alongside the package

## Usage

```csharp
// In your host:
engine.EnableImGui();

// In your graph (add an overlay pass):
builder.AddPass("imgui", pass => pass
    .Blends(GraphBuilder.Backbuffer)
    .Draw((ctx, _) =>
    {
        ImGui.Begin("Debug");
        // ...
        ImGui.End();
        ctx.DrawImGui();
    }));
```

## Dependencies

- [`Loom`](https://www.nuget.org/packages/Loom) — describe layer
- [`ImGui.NET`](https://www.nuget.org/packages/ImGui.NET) — Dear ImGui bindings (transitive)

Reference [`Loom.Sdk`](https://www.nuget.org/packages/Loom.Sdk) with `<UseImGui>true</UseImGui>` to pull this package in automatically.
