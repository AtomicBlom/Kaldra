# Getting Started with Loom

## Prerequisites

- .NET 10 SDK
- Windows (for DirectX12 and signed DXIL compilation), or any platform (Vulkan only)
- Vulkan SDK 1.4+ with validation layers (recommended for development)

## Consumption mode 1: NuGet (recommended for new projects)

Add to your `.csproj`:

```xml
<PackageReference Include="Loom.Sdk" Version="0.1.0-preview.1" />
```

Set backend flags before the reference (or in `Directory.Build.props`):

```xml
<PropertyGroup>
  <UseVulkan>true</UseVulkan>
  <UseDirectX12>false</UseDirectX12>
  <!-- Optional: UI overlay via SUIsei -->
  <UseSUIsei>false</UseSUIsei>
</PropertyGroup>
```

`Loom.Sdk` auto-injects props and targets. To compile your app's Slang shaders, add:

```xml
<PropertyGroup>
  <LoomShaderSourceDir>$(MSBuildProjectDirectory)\Shaders</LoomShaderSourceDir>
  <LoomShaderOutputMode>Content</LoomShaderOutputMode>
</PropertyGroup>
```

Slang source files in `Shaders/` compile to `.spv`/`.dxil`/`.json` under `shaders/` in your output.

## Consumption mode 2: Git submodule (for contributors or engine modifications)

```bash
git submodule add https://github.com/AtomicBlom/Loom external/Loom
git submodule update --init --recursive
```

In your `.csproj`:

```xml
<Import Project="external/Loom/build/Loom.Sdk/Sdk/Sdk.props" />

<PropertyGroup>
  <UseVulkan>true</UseVulkan>
  <LoomShaderSourceDir>$(MSBuildProjectDirectory)\Shaders</LoomShaderSourceDir>
  <LoomShaderOutputMode>Content</LoomShaderOutputMode>
</PropertyGroup>

<!-- … project body … -->

<Import Project="external/Loom/build/Loom.Sdk/Sdk/Sdk.targets" />
```

The submodule model gives you full engine source and the ability to modify the engine alongside your game. Both modes are first-class; there is no functional difference at runtime.

## Minimal example: two-pass render graph

This renders a scene to an offscreen HDR buffer, then tone-maps it to the swapchain.

### 1. Define shaders

`Shaders/scene.slang`:
```slang
struct VSOut { float4 pos : SV_Position; float3 color : COLOR; };

[shader("vertex")]
VSOut vertexMain(float3 pos : POSITION) {
    VSOut out;
    out.pos = mul(camera.viewProj, float4(pos, 1.0));
    out.color = float3(1, 0.5, 0);
    return out;
}

[shader("fragment")]
float4 fragmentMain(VSOut in) : SV_Target { return float4(in.color, 1.0); }
```

`Shaders/tonemap.slang` — see the `Loom.Sandbox` demos for a complete example.

### 2. Register techniques

```csharp
engine.DefineTechnique("scene", new TechniqueDescriptor
{
    VertexShader = "scene",
    FragmentShader = "scene",
    DepthTest = true,
    DepthWrite = true,
});

engine.DefineTechnique("tonemap", new TechniqueDescriptor
{
    VertexShader = "tonemap",
    FragmentShader = "tonemap",
});
```

### 3. Define the render graph

```csharp
var graph = RenderGraph.Define(g =>
{
    // Offscreen HDR colour + depth
    var hdr    = g.Color("hdr",   Format.R16G16B16A16_SFloat, g.Screen);
    var depth  = g.Depth("depth", Format.D32_SFloat,          g.Screen);

    // Scene pass: write to hdr + depth
    g.Pass("scene",
        pb => pb
            .Writes(hdr)
            .WritesDepth(depth)
            .Draw(ctx => ctx.Draw(mesh, "scene")));

    // Tonemap pass: sample hdr, write to backbuffer, present
    g.Pass("tonemap",
        pb => pb
            .Samples(hdr)
            .Writes(g.Backbuffer)
            .Present()
            .Draw(ctx => ctx.FullScreen("tonemap", hdr)));
});

engine.SetGraph(graph);
```

### 4. Render loop

```csharp
while (window.IsOpen)
{
    window.DoEvents();
    engine.Render(new FrameInputs
    {
        Camera    = camera,
        DeltaTime = (float)timer.Elapsed.TotalSeconds,
    });
}
```

## Creating an engine

```csharp
var settings = new VulkanEngineSettings
{
    Surface           = window.VkSurface,
    HdrOutput         = false,
    UseNamedGPUObjects = true,   // visible in GPU debuggers
};

using var engine = Engine.AvailableEngines["Vulkan"].Create(settings);
```

For DirectX 12:
```csharp
using var engine = Engine.AvailableEngines["DirectX12"].Create(new DirectX12EngineSettings
{
    WindowHandle = window.Handle,
});
```

`Engine.AvailableEngines` is populated by `[ModuleInitializer]` in each backend assembly. Including the backend's project / package reference is sufficient.

## Loading assets

```csharp
// Image → TextureData → Texture → Material
var data    = ImageLoader.FromFile("textures/albedo.png");
var texture = engine.CreateTexture(data);
var mat     = new Material([texture]);

// glTF
var model   = GltfLoader.FromFile("models/character.glb");
var mesh    = engine.CreateMesh(model.Vertices, model.Indices);
```
