# BinaryVibrance.Kaldra.Backend.WebGPU — browser WebGPU backend

`BinaryVibrance.Kaldra.Backend.WebGPU` is the browser WebGPU backend for the [Kaldra render-graph engine](https://github.com/AtomicBlom/Kaldra). It translates the engine's backend-neutral `GraphSchedule` and `BarrierPlan` into WebGPU calls, running under .NET WebAssembly.

## Requirements

- A browser with WebGPU support (`navigator.gpu`)
- .NET 11, published as WebAssembly (e.g. a Blazor WebAssembly host)
- .NET's WASM interop (`System.Runtime.InteropServices.JavaScript`) plus a JS shim that implements the device/command-buffer bridge — see `Kaldra.Web` in [Kaldra.Gallery](https://github.com/AtomicBlom/Kaldra.Gallery) for a working host

## What's included

- `WebGpuBackend` — the `IRenderBackend` registration; self-registers via `[ModuleInitializer]`, then constructs the engine from a `WebGpuSurface` your host builds after awaiting the JS shim's `initDevice`
- WGSL shaders embedded as resources (compiled from the same backend-neutral Slang source the other backends share, to WGSL instead of SPIR-V/DXIL)
- Per-frame GPU work is batched into one binary command buffer and replayed on the JS side, so the WASM↔JS interop boundary is crossed once per frame, not per draw

## Usage

```csharp
// After awaiting the JS shim's initDevice and building a WebGpuSurface from its caps:
IEngine engine = new WebGpuBackend().Create(surface, width, height, new WebGpuEngineSettings());
engine.DefineTechnique("lit", new TechniqueDescriptor("lit"));
engine.SetGraph(graph);
// each frame:
engine.Render(inputs);
```

Reference [`BinaryVibrance.Kaldra.Sdk`](https://www.nuget.org/packages/BinaryVibrance.Kaldra.Sdk) with `<UseWebGPU>true</UseWebGPU>` to pull this backend in automatically, or reference `BinaryVibrance.Kaldra.Backend.WebGPU` directly if you're building a custom WASM host.
