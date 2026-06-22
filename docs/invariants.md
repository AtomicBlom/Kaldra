# Loom Design Invariants

These are the load-bearing rules that make Loom tractable. Violating them silently breaks the design.

## 1. Derived, not declared

Users state reads and writes only. The engine derives:

- **Execution order** — topological sort from the read/write edges
- **Barriers and layout transitions** — from who last wrote and who next reads
- **Transient-resource aliasing** — from lifetime analysis
- **CPU↔GPU sync** — from frame-in-flight tracking

Any API that asks the user to specify *order* is a design smell. If you find yourself wanting to control barrier placement or pass ordering manually, the right move is to express the dependency through the resource access declarations.

## 2. `.Writes` vs `.Blends`

`.Writes(handle)` means **produce**: the engine may clear the target before this pass executes. Use it when this pass is the sole writer of that resource for this frame.

`.Blends(handle)` means **accumulate onto existing contents**: the engine preserves prior contents (`LoadOp.Load`) and the blend state accumulates on top. The pass is simultaneously the last reader and the next writer of that resource. Use it for multi-light forward passes, particle accumulation, and any other "layer on top of" pattern.

The barrier derivation depends on the distinction. Getting them swapped produces incorrect rendering, not a runtime error.

## 3. Backend isolation

All procedural, struct-heavy, graphics-API-specific code lives in the backends (`Loom.Backend.Vulkan`, `Loom.Backend.DirectX12`). `Loom.Core` and `Loom.Graph` carry no Vulkan or D3D12 type.

The dependency direction enforces this: `Sandbox → Backend → Graph → Core`. A reference in the opposite direction (Core → Backend) is the cardinal violation; it breaks the backend-portability guarantee and prevents a third backend from joining.

## 4. Pure per-frame closures

`.Draw` and `.Dispatch` callbacks pull their inputs from `ctx` (`ctx.Camera`, `ctx.Time`, `ctx.SunView`, `ctx.HoverId`), rather than capturing them at graph-definition time. This keeps the graph description frame-independent and cacheable: the same `RenderGraph` value drives every frame, with per-frame data arriving through `IEngine.Render(frameInputs)`.

**Correct**:
```csharp
pass.Draw(ctx => ctx.Draw(mesh, "gbuffer"));
```
**Incorrect** (captures camera at definition time):
```csharp
var camera = GetCamera();   // stale every frame
pass.Draw(ctx => ctx.Draw(mesh, "gbuffer", camera));  // don't capture
```

## 5. Handle swap for ping-pong resources

Iterative effects (bloom, Jump Flood Algorithm) need two physical resources alternating between read and write. The pattern: allocate two handles, keep a `(read, write)` pair, swap them each iteration. The graph follows the handles.

```csharp
var (readBuf, writeBuf) = (g.Color("jfa-a", fmt, domain), g.Color("jfa-b", fmt, domain));
for (var i = 0; i < iterations; i++)
{
    g.Pass($"jfa-step-{i}", pb => pb.Samples(readBuf).Writes(writeBuf).Draw(ctx => …));
    (readBuf, writeBuf) = (writeBuf, readBuf);   // swap
}
```

## 6. Vulkan 1.3 baseline

The Vulkan backend requires dynamic rendering, synchronization2, and timeline semaphores as device features. No renderpass or framebuffer objects; barriers use `vkCmdPipelineBarrier2`. A device that doesn't support these features is rejected at engine creation.

## 7. Structural changes are cheap and cached

`RenderGraph` is immutable and hashable. Calling `IEngine.SetGraph` with a structurally identical graph (same hash) is a no-op — the cached baked schedule is reused. A genuinely new graph rebuilds the schedule and updates pipeline state, but the device and pipeline cache persist across graph swaps, so subsequent frames with the new graph are warm from the first frame.

The consequence: it is safe (and idiomatic) to call `RenderGraph.Define` every frame if the graph structure is conditional on runtime state. Only structural changes (different passes, different resources, different domains) trigger a rebake; per-frame data travels via `FrameInputs` and `ctx`.
