# Loom.Concepts — the app/scene concept layer

`Loom.Concepts` is the app/scene concept layer for the [Loom render-graph engine](https://github.com/AtomicBlom/Loom) — the concepts the render-graph engine deliberately does not have, built over its neutral channels (`Constants` / per-draw `Uniforms` / `Storage` / streams). It depends only on `Loom.Core`'s public API, so it works with any backend (`Loom.Backend.Vulkan`, `Loom.Backend.DirectX12`, `Loom.Backend.WebGPU`).

## Requirements

- .NET 10
- No graphics-API reference

## What's included

- `Camera` — a view/projection helper that composes the engine's `IEngineClipSpace.ClipSpaceCorrection` so a view-projection matrix lands in clip space correctly on every backend without the app naming a backend's NDC convention. Set the view with `LookAt`/`FreeLook`/`Chase`, the projection with `Perspective`/`Orthographic`, read back `ViewProjection` each frame, and `ExtractFrustum()` for CPU-side culling.
- `LeasedBuffer<T>` / `BufferLease<T>` — a packed, dirty-tracked storage buffer for per-object data (transforms, material indices, ...). Each `BufferLease<T>` owns one row; disposing a lease compacts the buffer by moving its last row into the freed slot.

## Usage

```csharp
var camera = new Camera(engine)
	.Perspective(fovY: 1.2f, aspect: 16f / 9f, near: 0.1f, far: 100f)
	.LookAt(eye: new Vector3(0, 2, 5), target: Vector3.Zero);

// each frame, publish to a Constants<Matrix4x4> buffer a geometry pass .Reads:
engine.Write(viewProjectionHandle, camera.ViewProjection);
```

Scene composition and frustum-culled draw submission over `LeasedBuffer<T>` are on the roadmap; `Camera` and the buffer-leasing primitives are what ships today.
