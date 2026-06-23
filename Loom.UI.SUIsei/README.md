# Loom.UI.SUIsei — SUIsei integration

`Loom.UI.SUIsei` bridges the [SUIsei](https://github.com/AtomicBlom/SUIsei) retained-mode UI runtime with the [Loom render-graph engine](https://github.com/AtomicBlom/Loom), routing SUIsei's compositor output through Loom's backend-neutral draw-list API.

## What it does

- Implements `IUICompositor` using Loom's `IRenderContext.DrawList` — neither the Vulkan nor the DirectX 12 backend references SUIsei directly
- Provides `LoomUIRenderer` — call it from a pass's `.Draw` callback to submit the current frame's SUIsei draw lists
- Includes UI shaders (compiled from Slang) distributed as content files alongside the package

## Usage

```csharp
// In your host:
engine.EnableUI(font.Texture);   // registers the UI compositor

// In your graph:
builder.AddPass("ui", pass => pass
    .Blends(GraphBuilder.Backbuffer)
    .Draw((ctx, _) => ctx.DrawUI(uiHost.DrawLists)));
```

## Dependencies

- [`Loom`](https://www.nuget.org/packages/Loom) — describe layer
- [`SUIsei`](https://www.nuget.org/packages/SUIsei) — UI runtime (transitive)

Reference [`Loom.Sdk`](https://www.nuget.org/packages/Loom.Sdk) with `<UseSUIsei>true</UseSUIsei>` to pull this package in automatically alongside the selected backend.
