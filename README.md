# Loom

A declarative, render-graph-driven rendering engine for .NET 10.

You declare *what* each render pass reads and writes; the engine derives ordering, barriers, image-layout transitions, transient memory aliasing, and CPU↔GPU synchronisation. Procedural graphics-API code is quarantined in a backend layer and never leaks into the authoring API.

## Packages

| Package | Description |
|---|---|
| `Loom` | Describe layer — `GraphBuilder`, resource handles, `Domain`, technique builders |
| `Loom.Graph` | Execute layer — scheduling, barrier/layout derivation, transient aliasing |
| `Loom.Assets` | Neutral image (StbImageSharp) and glTF (SharpGLTF) loaders |
| `Loom.Backend.Vulkan` | Vulkan 1.3 backend (dynamic rendering, sync2, timeline semaphores) |
| `Loom.Backend.DirectX12` | DirectX 12 backend (placed-resource aliasing, PSOs) |
| `Loom.UI.SUIsei` | SUIsei retained-mode UI integration |
| `Loom.UI.ImGui` | Dear ImGui integration |
| `Loom.Sdk` | MSBuild SDK entry point — reference this from a game project |

## Quick start

```xml
<!-- game.csproj -->
<PackageReference Include="Loom.Sdk" Version="0.1.0-preview.1" />
```

```xml
<!-- Directory.Build.props or game.csproj -->
<UseVulkan>true</UseVulkan>
<UseDirectX12>false</UseDirectX12>
```

## Architecture

Three layers, kept strictly separate:

- **Describe** (`Loom`) — immutable, hashable, fluent builders; the only layer authored against directly.
- **Execute** (`Loom.Graph`) — derives barriers, layout transitions, and aliasing decisions; backend-neutral.
- **Bake + record** (`Loom.Backend.*`) — marshals descriptions to native structs, creates cached pipelines, and emits real GPU work.

## Links

- [GitHub](https://github.com/AtomicBlom/Loom)
- [Docs](https://github.com/AtomicBlom/Loom/tree/master/docs)
