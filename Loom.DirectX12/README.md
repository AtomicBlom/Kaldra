# Loom.Backend.DirectX12 — DirectX 12 backend

`Loom.Backend.DirectX12` is the DirectX 12 backend for the [Loom render-graph engine](https://github.com/AtomicBlom/Loom). It translates the engine's backend-neutral `GraphSchedule` and `BarrierPlan` into Direct3D 12 command lists.

## Requirements

- Windows 10/11 with a D3D12-capable GPU (feature level 12.0+)
- Windows SDK / DXC for signed DXIL (shaders are pre-compiled and embedded)
- .NET 10

## What's included

- `DirectX12Engine` — `IEngine` implementation; owns the D3D12 device, command allocators, PSO cache, mesh store, descriptor heaps, and swapchain
- Offline signed DXIL shaders embedded as resources (compiled from Slang at build time)
- Placed-resource aliasing of transient targets; D3D12 resource barriers derived from the engine plan
- Silk.NET 2.23 bindings

## Usage

```csharp
// In your host (Silk.NET window):
IEngine engine = DirectX12EngineFactory.Create(window.Handle, new DirectX12EngineSettings());
engine.DefineTechnique("lit", new TechniqueDescriptor("lit"));
engine.SetGraph(graph);
// each frame:
engine.Render(inputs);
```

Reference [`Loom.Sdk`](https://www.nuget.org/packages/Loom.Sdk) with `<UseDirectX12>true</UseDirectX12>` to pull this backend in automatically.
