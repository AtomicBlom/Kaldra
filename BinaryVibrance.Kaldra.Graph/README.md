# BinaryVibrance.Kaldra.Graph — execute layer

`BinaryVibrance.Kaldra.Graph` is the backend-neutral scheduling core of the [Kaldra render-graph engine](https://github.com/AtomicBlom/Kaldra). Given an immutable `RenderGraph` description, it produces a `GraphSchedule` + `BarrierPlan` + transient-memory aliasing plan — one per graph bake, stable across frames — that the backends consume to record GPU commands.

## What it does

- **Topological sort** — orders passes by their read/write edges; detects cycles
- **Barrier derivation** — computes which resources need pipeline barriers and image-layout transitions between passes, from the declared `.Writes` / `.Samples` / `.Blends` / `.ReadWrites` edges
- **Transient memory aliasing** — identifies non-overlapping resource lifetimes and aliases their backing memory, reducing VRAM usage
- **Lifetime analysis** — tracks when each resource is first written and last read, for aliasing and for issuing the correct `LoadOp` / `StoreOp`

## Design

No graphics-API types cross this layer. The plan is expressed in terms of abstract resources and intents (`ResourceAccess`, `BarrierEdge`, `LayoutTransition`) that the Vulkan and DirectX 12 backends each translate to their own barrier model. Adding a third backend requires no changes here.

## Relationship to other packages

`BinaryVibrance.Kaldra.Graph` depends on [`BinaryVibrance.Kaldra.Core`](https://www.nuget.org/packages/BinaryVibrance.Kaldra.Core) (the describe layer) and is a transitive dependency of both backends — you do not reference it directly in a game project.
