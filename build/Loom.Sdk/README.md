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
<UseWebGPU>false</UseWebGPU>
<UseSUIsei>false</UseSUIsei>
<UseImGui>false</UseImGui>
```

Setting a flag to `true` causes `Loom.Sdk.props` to add the corresponding `PackageReference` automatically — you never need to reference `Loom.Backend.Vulkan` or `Loom.UI.SUIsei` directly. `UseWebGPU` targets browser/WASM hosts (e.g. Blazor WebAssembly); it's independent of the desktop backend flags, though a given app typically enables only one.

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
| `UseWebGPU=true` | `Loom.Backend.WebGPU` |
| `UseSUIsei=true` | `Loom.UI.SUIsei` + `SUIsei`, with SUIsei's XAML generator |
| `UseImGui=true` | `Loom.UI.ImGui` + `ImGui.NET` |

## XAML views

`UseSUIsei=true` also gives the app SUIsei's XAML:
- Every `*.xaml` under the project compiles into its partial `x:Class`, with typed `x:Name` fields and `InitializeComponent()`.
- XAML errors fail the build at the view's file and line.
- `dotnet watch` hot-reloads an edited view.

Two settings shape it:
- `SUIseiXamlSourceDir` narrows the search to one folder.
- `SUIseiXamlEnable=false` turns the generator off.

Folders kept out of the project's items through `DefaultItemExcludes` are kept out of its XAML too. For example, a game that holds Loom as a submodule beneath it, and excludes `Loom/**`, does not compile Loom's own views.

The `SUIsei` package is referenced directly because the generator travels in it. A package's analyzers and build files do not reach an app through `Loom.UI.SUIsei`'s dependency on it. The version is `LoomSUIseiVersion`.
