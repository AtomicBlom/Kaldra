# Loom's Pillars of Design

These are the load-bearing rules behind Loom's design. Each is easy to break without a compiler error and hard to notice once broken — violate one and the design quietly stops working. Hold them when adding or changing anything in the engine.

## Derived, not declared

Users state reads and writes only. The engine derives:

- **Execution order** — topological sort from the read/write edges.
- **Barriers and layout transitions** — from who last wrote and who next reads.
- **Transient-resource aliasing** — from lifetime analysis (non-overlapping lifetimes share memory).
- **CPU↔GPU sync** — from frame-in-flight tracking.

Any API that asks the user to specify *order* is a smell. If you find yourself wanting to place a barrier or sequence two passes by hand, express the dependency through a resource access instead and let the derivation do it.

## `.Writes` vs `.Blends`

`.Writes(handle)` means **produce**: the engine may clear the target before the pass runs. Use it when the pass is the sole writer of that resource this frame.

`.Blends(handle)` means **accumulate onto existing contents**: the engine preserves what's there (`LoadOp.Load`) and the technique's blend state layers on top. The pass is simultaneously the last reader and the next writer. Use it for multi-light forward passes, particle accumulation, and any "layer on top of" pattern.

The barrier derivation depends on the distinction. Getting them swapped produces wrong pixels, not a runtime error.

## Backend isolation / portability

All procedural, struct-heavy, graphics-API code lives in the backends (`Loom.Backend.Vulkan`, `Loom.Backend.DirectX12`). `Loom.Core` and `Loom.Graph` carry no Vulkan or D3D12 type.

The dependency direction enforces it: `Sandbox → Backend → Graph → Core`. A reference the other way (Core → Backend) is the cardinal violation — it breaks portability and blocks a new backend from joining. Loom runs on Vulkan and DirectX 12 in parallel at pixel parity today, with Metal and WebGPU under investigation; that future depends on this isolation staying clean, so every new GPU feature must land in (or be capability-gated for) every backend.

## Pure per-frame closures

The graph is built once with `RenderGraph.Define` and cached; each `.Draw`/`.Dispatch` body then runs every frame. So a closure may capture **stable objects** — engine handles and app-side stable producers (a painter, a scene) — and read their **live** members at execute time; it must **not** capture a per-frame **value**, because that snapshot freezes at build time and never updates. Pull per-frame facts from `ctx` and per-frame data from neutral `Constants`/`Storage` buffers (`IEngine.Write(handle, …)`), or from a stable producer passed in as pass state.

**Correct** — pull a per-frame matrix from a constants channel the pass reads:

```csharp
var viewProj = g.Constants<Matrix4x4>("camera.viewproj");
g.Pass("gbuffer")
    .Reads(viewProj)
    .Writes(albedo)
    .Draw(static ctx => ctx.Draw(DrawCall.Create(mesh, "gbuffer")));
// each frame, before Render: engine.Write(viewProj, camera.ViewProjection);
```

**Incorrect** — capturing a value at definition time freezes it:

```csharp
var camera = GetCamera();          // stale every frame after the first
g.Pass("gbuffer").Writes(albedo)
    .Draw(ctx => DrawWith(ctx, camera));   // don't capture per-frame data
```

**Correct** — a stable producer (not a per-frame value) passed as pass state, so the body can be `static`:

```csharp
g.Pass("gbuffer").Writes(albedo)
    .Draw(scene, static (ctx, scene) => DrawWith(ctx, scene));   // scene.Camera read live, not snapshotted
```

A stable handle bundle created once for the engine's lifetime (a painter, a mesh store) is fine to capture directly — it isn't per-frame data. The `Draw<TState>`/`Dispatch<TState>` overloads exist for the cases where that stable state isn't already in scope: pass it explicitly and write the body `static`, so the compiler rejects any accidental capture of a per-frame value.

## No allocations on the hot path

The steady per-frame render loop allocates no managed memory (especially in Release). The graph is built once and cached; per-frame work runs through preallocated/pooled buffers, `ref struct`s, and spans. Scratch that grows on demand grows geometrically and then stays put, so steady state settles to zero allocations. This applies equally to Vysara's per-frame tick/route/composite path.

A `foreach` over an interface, a LINQ chain, a closure capture, or a boxed struct inside `.Draw` / `.Dispatch` / the compositor is a regression — reach for an indexed `for`, a pooled array, or a value type.

## The engine names no game concepts

There is no `Camera`, `Sun`, `Scene`, or `Cascade` in `Loom.Core` or `Loom.Graph`. View, light, and time data ride in through neutral channels — the `Constants` and `Storage` buffers a pass reads, and the `FrameInputs` the app fills — and the app names its own views with its own keys. `Camera`, `Frustum`, and `Scene` exist only as app-side helpers in `Loom.Concepts`: convenience, not engine vocabulary.

When a feature seems to need a named game concept in the engine, generalise it into a neutral channel the app fills instead.

## Ping-pong via handle swap

Iterative effects (bloom, the Jump Flood Algorithm) need two physical resources alternating between read and write. The pattern: allocate two handles, keep a `(read, write)` pair, swap them each iteration. The graph follows the handles — there is no special "ping-pong" concept in the engine.

```csharp
var (read, write) = (g.Screen.Color("jfa-a", Format.Rg16F), g.Screen.Color("jfa-b", Format.Rg16F));
for (var i = 0; i < iterations; i++)
{
    g.Pass($"jfa-{i}").Samples(read).Writes(write).Draw(ctx => …);
    (read, write) = (write, read);   // swap; the next pass reads what this one wrote
}
```

## Fail loud, never silently degrade

A backend mapping switch throws on a value it doesn't map rather than guessing. An API that over-promises throws at derive time rather than quietly doing the wrong thing. A device missing a required feature is rejected at engine creation, not worked around. The design would rather stop with a clear error than ship a subtly wrong frame.

---

## Other load-bearing properties

Not pillars, but engine guarantees worth knowing when working on the backends or the schedule:

- **Structural changes are cheap and cached.** `RenderGraph` is immutable and hashable, and `RenderGraph.Hash()` covers everything the Execute layer derives from — resources (including their clear attributes), passes, access edges, the present and readback targets — so equal hashes mean equal derived plans and equal GPU resource needs. `IEngine.SetGraph` with a structurally identical graph therefore skips the whole GPU half: no drain, no reallocation, and no loss of what those resources hold (`Persistent` history, written parameter buffers, pending readback). It still re-derives the schedule/barriers, because the plan carries *this* graph's `.Draw`/`.Dispatch` callbacks and reusing the previous graph's would silently render whatever the old closures captured; deriving is pure CPU work over immutable data, so it costs a fraction of the rebuild it replaces. A genuinely new graph rebakes, but the device and pipeline cache persist across swaps, so the new graph is warm from its first frame. It is therefore safe to call `RenderGraph.Define` every frame if the structure depends on runtime state: only structural changes (different passes, resources, or domains) trigger a rebake, while per-frame data travels via `FrameInputs` and `ctx`.
- **Vulkan baseline.** The Vulkan backend requires dynamic rendering, synchronization2, and timeline semaphores; barriers use `vkCmdPipelineBarrier2`, and there are no renderpass or framebuffer objects. A device without these features is rejected at engine creation (per *Fail loud* above).
