# Loom.Backend.Vulkan — Vulkan 1.3 backend

`Loom.Backend.Vulkan` is the Vulkan backend for the [Loom render-graph engine](https://github.com/AtomicBlom/Loom). It translates the engine's backend-neutral `GraphSchedule` and `BarrierPlan` into real Vulkan command buffers.

## Requirements

- Vulkan 1.3 device with **dynamic rendering**, **synchronization2**, and **timeline semaphores** (enabled as device features — no fallback)
- Windows, Linux, or macOS (via MoltenVK)
- .NET 10

## What's included

- `VulkanEngine` — `IEngine` implementation; owns the device, pipeline cache, mesh store, descriptor allocator, and swapchain
- Offline SPIR-V shaders embedded as resources (compiled from Slang at build time)
- VulkanMemoryAllocator (VMA) bundled — no separate package reference needed
- 2 frames in flight; `vkCmdPipelineBarrier2` for all transitions; placed-resource aliasing of transients
- Silk.NET 2.23 bindings

## Usage

```csharp
// In your host (Silk.NET window):
IEngine engine = VulkanEngineFactory.Create(window.VkSurface!, new VulkanEngineSettings());
engine.DefineTechnique("lit", new TechniqueDescriptor("lit"));
engine.SetGraph(graph);
// each frame:
engine.Render(inputs);
```

Reference [`Loom.Sdk`](https://www.nuget.org/packages/Loom.Sdk) with `<UseVulkan>true</UseVulkan>` to pull this backend in automatically.
