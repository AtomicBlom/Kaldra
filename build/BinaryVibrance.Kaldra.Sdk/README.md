# BinaryVibrance.Kaldra.Sdk — MSBuild SDK entry point

`BinaryVibrance.Kaldra.Sdk` is the single package reference a game project needs to use the [Kaldra render-graph engine](https://github.com/AtomicBlom/Kaldra). It injects MSBuild props that pull in the selected backends and UI integrations, and provides the Slang shader-compile targets for game-side shaders.

## Usage

```xml
<!-- game.csproj -->
<PackageReference Include="BinaryVibrance.Kaldra.Sdk" Version="0.1.0-preview.1" />
```

```xml
<!-- Directory.Build.props or game.csproj -->
<UseVulkan>true</UseVulkan>
<UseDirectX12>false</UseDirectX12>
<UseWebGPU>false</UseWebGPU>
<UseVysara>false</UseVysara>
<UseImGui>false</UseImGui>
```

Setting a flag to `true` causes `BinaryVibrance.Kaldra.Sdk.props` to add the corresponding `PackageReference` automatically — you never need to reference `BinaryVibrance.Kaldra.Backend.Vulkan` or `BinaryVibrance.Kaldra.UI.Vysara` directly. `UseWebGPU` targets browser/WASM hosts (e.g. Blazor WebAssembly); it's independent of the desktop backend flags, though a given app typically enables only one.

## Shader compilation

Place Slang source files under your project and set `KaldraShaderOutputMode=Content` to have them compiled to SPIR-V + DXIL at build time and distributed alongside your game:

```xml
<KaldraShaderSourceDir>Shaders/</KaldraShaderSourceDir>
<KaldraShaderOutputMode>Content</KaldraShaderOutputMode>
```

## What's injected

| Flag | Package added |
|---|---|
| `UseVulkan=true` | `BinaryVibrance.Kaldra.Backend.Vulkan` |
| `UseDirectX12=true` | `BinaryVibrance.Kaldra.Backend.DirectX12` |
| `UseWebGPU=true` | `BinaryVibrance.Kaldra.Backend.WebGPU` |
| `UseVysara=true` | `BinaryVibrance.Kaldra.UI.Vysara` + `BinaryVibrance.Vysara`, with Vysara's XAML generator |
| `UseImGui=true` | `BinaryVibrance.Kaldra.UI.ImGui` + `ImGui.NET` |

## XAML views

`UseVysara=true` also gives the app Vysara's XAML:
- Every `*.xaml` under the project compiles into its partial `x:Class`, with typed `x:Name` fields and `InitializeComponent()`.
- XAML errors fail the build at the view's file and line.
- `dotnet watch` hot-reloads an edited view.

Two settings shape it:
- `VysaraXamlSourceDir` narrows the search to one folder.
- `VysaraXamlEnable=false` turns the generator off.

Folders kept out of the project's items through `DefaultItemExcludes` are kept out of its XAML too. For example, a game that holds Kaldra as a submodule beneath it, and excludes `Kaldra/**`, does not compile Kaldra's own views.

The `BinaryVibrance.Vysara` package is referenced directly because the generator travels in it. A package's analyzers and build files do not reach an app through `BinaryVibrance.Kaldra.UI.Vysara`'s dependency on it. The version is `KaldraVysaraVersion`.
