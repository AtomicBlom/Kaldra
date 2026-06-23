# Loom.Sdk — MSBuild SDK entry point

`Loom.Sdk` is the single package reference a game project needs to use the [Loom render-graph engine](https://github.com/AtomicBlom/Loom). It injects MSBuild props that pull in the selected backends and UI integrations, and provides the Slang shader-compile targets for game-side shaders.

## Usage

```xml
<!-- game.csproj -->
<PackageReference Include="Loom.Sdk" Version="0.1.0-preview.1" />
```

```xml
<!-- Directory.Build.props or game.csproj -->
<UseVulkan>true</UseVulkan>
<UseDirectX12>false</UseDirectX12>
<UseSUIsei>false</UseSUIsei>
<UseImGui>false</UseImGui>
```

Setting a flag to `true` causes `Loom.Sdk.props` to add the corresponding `PackageReference` automatically — you never need to reference `Loom.Backend.Vulkan` or `Loom.UI.SUIsei` directly.

## Shader compilation

Place Slang source files under your project and set `LoomShaderOutputMode=Content` to have them compiled to SPIR-V + DXIL at build time and distributed alongside your game:

```xml
<LoomShaderSourceDir>Shaders/</LoomShaderSourceDir>
<LoomShaderOutputMode>Content</LoomShaderOutputMode>
```

## What's injected

| Flag | Package added |
|---|---|
| `UseVulkan=true` | `Loom.Backend.Vulkan` |
| `UseDirectX12=true` | `Loom.Backend.DirectX12` |
| `UseSUIsei=true` | `Loom.UI.SUIsei` (brings `SUIsei` transitively) |
| `UseImGui=true` | `Loom.UI.ImGui` + `ImGui.NET` |
