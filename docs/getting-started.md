# Getting Started with Kaldra

Kaldra is a declarative, render-graph-driven rendering engine for .NET 11, running on **Vulkan and DirectX 12 at pixel parity**. You declare *what* each pass reads and writes; the engine derives ordering, barriers, image-layout transitions, transient-memory aliasing, and CPU↔GPU synchronisation.

This guide takes you from an empty project to a two-pass render graph drawing a mesh to an offscreen HDR target and tone-mapping it to the screen. For the design rationale see [`docs/pillars.md`](pillars.md) and [`docs/architecture.md`](architecture.md); for the full feature list see [`README.md`](../README.md).

## Prerequisites

- .NET 11 SDK
- Windows (for DirectX 12 and signed DXIL compilation), or any platform (Vulkan only)
- Vulkan SDK 1.4+ with validation layers (recommended for development)

## Consumption mode 1: NuGet (recommended for new projects)

Add the SDK package to your `.csproj`:

```xml
<PackageReference Include="BinaryVibrance.Kaldra.Sdk" Version="0.1.0-preview.1" />
```

`BinaryVibrance.Kaldra.Sdk` auto-injects the props and targets that pull in the backends. Select which backends to reference with MSBuild flags, set before the reference (or in `Directory.Build.props`):

```xml
<PropertyGroup>
  <UseVulkan>true</UseVulkan>
  <UseDirectX12>false</UseDirectX12>
  <!-- Optional UI integrations -->
  <UseVysara>false</UseVysara>
  <UseImGui>false</UseImGui>
</PropertyGroup>
```

Setting a flag to `true` adds the matching `PackageReference` automatically — you never reference `BinaryVibrance.Kaldra.Backend.Vulkan` or `BinaryVibrance.Kaldra.UI.Vysara` directly, and `BinaryVibrance.Kaldra.Core` / `BinaryVibrance.Kaldra.Graph` / `BinaryVibrance.Kaldra.Assets` arrive transitively. `UseVysara` also brings Vysara's XAML generator: every `.xaml` view in the project compiles into its partial `x:Class`, XAML errors fail the build at the view's file and line, and `dotnet watch` hot-reloads an edited view. To compile your app's Slang shaders at build time, add:

```xml
<PropertyGroup>
  <KaldraShaderSourceDir>$(MSBuildProjectDirectory)\Shaders</KaldraShaderSourceDir>
  <KaldraShaderOutputMode>Content</KaldraShaderOutputMode>
</PropertyGroup>
```

Slang sources in `Shaders/` compile to `.spv` / `.dxil` / `.json` under `shaders/` in your output, beside the executable — exactly where the backends resolve a technique's logical shader name.

## Consumption mode 2: Git submodule (for contributors or engine modifications)

```bash
git submodule add https://github.com/AtomicBlom/Kaldra external/Kaldra
git submodule update --init --recursive
```

An app with `UseVysara` builds Vysara from a checkout too: by default `Vysara` beside the engine's checkout
(`external/Vysara` here), or the folder the `VysaraSource` property names (`build/Vysara.Source.props`).

Consume the SDK by writing the two import lines yourself:

```xml
<Import Project="external/Kaldra/build/BinaryVibrance.Kaldra.Sdk/Sdk/Sdk.props" />

<PropertyGroup>
  <UseVulkan>true</UseVulkan>
  <KaldraShaderSourceDir>$(MSBuildProjectDirectory)\Shaders</KaldraShaderSourceDir>
  <KaldraShaderOutputMode>Content</KaldraShaderOutputMode>
</PropertyGroup>

<!-- … project body … -->

<Import Project="external/Kaldra/build/BinaryVibrance.Kaldra.Sdk/Sdk/Sdk.targets" />
```

The submodule model gives you full engine source and the ability to modify the engine alongside your game. Both modes are first-class; there is no functional difference at runtime.

## Creating an engine

A backend is the factory for an `IEngine`. Each referenced backend assembly self-registers an `IRenderBackend` into `Backends.Available` as it loads, so the host discovers what is available at runtime and selects one by name. **Constructing a backend's settings subclass (`VulkanEngineSettings` / `DirectX12EngineSettings`) is what loads that backend's assembly** and fires its registration — it is the one sanctioned place a backend type appears in app code.

The host hands the backend the window's native surface *opaquely* — Kaldra names no windowing type. For Vulkan that is the window's `IVkSurface`; for DirectX 12 it is the window's `HWND`. Using Silk.NET for windowing:

```csharp
using BinaryVibrance.Kaldra.Core.Hosting;
using BinaryVibrance.Kaldra.Backend.Vulkan;

// A Vulkan-capable window (Silk.NET).
var options = WindowOptions.DefaultVulkan with { Size = new Vector2D<int>(1280, 720), Title = "My Kaldra app" };
var window = Window.Create(options);
window.Initialize();

if (window.VkSurface is null)
    throw new InvalidOperationException("The windowing platform did not expose a Vulkan surface.");

// Constructing VulkanEngineSettings loads BinaryVibrance.Kaldra.Backend.Vulkan, which registers the backend.
var settings = new VulkanEngineSettings
{
    EnableValidation = true,        // Vulkan validation layers (dev)
    VSync           = true,
    HdrOutput       = false,
    PreferredBackend = "Vulkan",
};

// Select the backend by name and create the engine bound to the surface.
var backend = Backends.Available.FirstOrDefault(b => b.Name == settings.PreferredBackend)
              ?? Backends.Available.First();
using var engine = backend.Create(window.VkSurface, window.FramebufferSize.X, window.FramebufferSize.Y, settings);
```

For DirectX 12, create a context-less window (`API = GraphicsAPI.None`), construct `DirectX12EngineSettings` (which loads `BinaryVibrance.Kaldra.Backend.DirectX12`), and pass the window's `HWND` as the surface:

```csharp
using BinaryVibrance.Kaldra.Backend.DirectX12;

var settings = new DirectX12EngineSettings
{
    EnableValidation = true,        // D3D12 debug layer (dev)
    PreferredBackend = "DirectX12",
};

var hwnd = window.Native?.Win32?.Hwnd
           ?? throw new InvalidOperationException("The DirectX12 backend needs the window's Win32 HWND.");

var backend = Backends.Available.FirstOrDefault(b => b.Name == settings.PreferredBackend)
              ?? Backends.Available.First();
using var engine = backend.Create(hwnd, window.FramebufferSize.X, window.FramebufferSize.Y, settings);
```

The neutral `EngineSettings` base carries `EnableValidation`, `VSync`, `MaxFramesPerSecond`, `HdrOutput`, `PreferredBackend`, and `WatchShaderDirectory` (dev shader hot-reload). Each subclass adds its own knobs (e.g. `PreferDiscreteGpu`, a `ShaderDirectory`). Call `engine.Resize(width, height)` when the window resizes, and dispose the engine (it drains the GPU first) on shutdown.

## Minimal example: two-pass render graph

This renders a mesh into an offscreen HDR buffer, then tone-maps that buffer to the swapchain. It shows the whole loop: shaders, techniques, a graph, and the per-frame call.

### 1. Define shaders

Author shaders in **Slang**; the SDK compiles them offline to SPIR-V (Vulkan), signed DXIL (DirectX 12), and WGSL (WebGPU), and the backend derives descriptor-binding layouts from the emitted reflection JSON — you never hand-count bindings, nor hand-order them. The technique descriptor's default entry points are `vertexMain` / `fragmentMain`, so name your stages to match.

Every shader starts by including the platform module, which owns the places the three targets disagree. [`shader-authoring.md`](shader-authoring.md) is the full contract; the two rules you need here are that resources take **no binding numbers or registers** (Slang numbers them per target, in declaration order, and each backend binds by its reflection), and that a shader writing clip space directly uses a `KALDRA_*_POS` macro rather than doing it by hand.

`Shaders/scene.slang` — a lit mesh. It reads the per-frame view-projection from a constants buffer the pass `.Reads`, and a per-draw model matrix from the per-draw uniform block:

```slang
#include "kaldra_platform.module.slang"

struct ViewProj { float4x4 viewProj; }   // filled from the app via engine.Write(viewProj, …)
struct PerDraw  { float4x4 model; }      // staged by DrawCall.SetUniforms(ref block)

// No numbers: Slang gives each target its own, in declaration order.
// Uniform slots fill in a defined order: the pass's .Reads constants first, then the per-draw block.
ConstantBuffer<ViewProj> view;
ConstantBuffer<PerDraw>  draw;

struct VSOut { float4 pos : SV_Position; float3 normal : NORMAL; }

[shader("vertex")]
VSOut vertexMain(float3 pos : POSITION, float3 normal : NORMAL)
{
    VSOut o;
    float4 world = mul(draw.model, float4(pos, 1.0));
    o.pos = mul(view.viewProj, world);
    o.normal = mul((float3x3)draw.model, normal);
    return o;
}

[shader("fragment")]
float4 fragmentMain(VSOut i) : SV_Target
{
    float light = saturate(dot(normalize(i.normal), normalize(float3(0.4, 0.8, 0.3))));
    return float4(float3(1.0, 0.6, 0.2) * (0.2 + 0.8 * light), 1.0);
}
```

`Shaders/tonemap.slang` — a fullscreen pass that samples the HDR target and maps it to display range. A fullscreen technique fabricates its own triangle, so it has no vertex input:

```slang
#include "kaldra_platform.module.slang"

Sampler2D hdr;   // the pass's first .Samples input

struct VSOut { float4 pos : SV_Position; float2 uv : TEXCOORD0; }

[shader("vertex")]
VSOut vertexMain(uint id : SV_VertexID)
{
    // Fullscreen triangle from the vertex id — no mesh. KALDRA_FULLSCREEN_POS applies the NDC-Y convention this
    // target needs; writing `uv * 2 - 1` by hand renders upside down on WebGPU.
    float2 uv = float2((id << 1) & 2, id & 2);
    VSOut o;
    o.pos = KALDRA_FULLSCREEN_POS(uv);
    o.uv = uv;
    return o;
}

[shader("fragment")]
float4 fragmentMain(VSOut i) : SV_Target
{
    float3 c = hdr.Sample(i.uv).rgb;
    c = c / (c + 1.0);                       // Reinhard tone-map
    return float4(c, 1.0);
}
```

### 2. Register techniques

A technique names its shader *logically* and states its pipeline intent. `HasVertexInput: true` means the technique reads a mesh's vertex stream; left default, the technique fabricates a fullscreen triangle (the tone-map). Register both up front, before the graph draws with them:

```csharp
using BinaryVibrance.Kaldra.Core.Materials;

engine.DefineTechnique("scene", new TechniqueDescriptor("scene", HasVertexInput: true));
engine.DefineTechnique("tonemap", new TechniqueDescriptor("tonemap"));
```

### 3. Define the render graph

A graph is built once with `RenderGraph.Define` and set on the engine with `SetGraph`. You declare each pass's reads and writes — never the order or the barriers — and `g.Present` names the target shown on screen. Per-frame view data rides a neutral `Constants` channel the scene pass reads; the engine names no camera.

The graph is immutable and cached by hash, so re-setting a structurally identical one is cheap: the engine reuses the GPU resources it already built (and everything they hold — cross-frame `Persistent` history, your written parameter buffers) and only re-derives the plan. You can rebuild and set the graph whenever the structure *might* have changed rather than tracking that by hand; only a real structural change pays for a rebuild.

```csharp
using System.Numerics;
using BinaryVibrance.Kaldra.Core.Graph;
using BinaryVibrance.Kaldra.Core.Hosting;

var graph = RenderGraph.Define(g =>
{
    // Offscreen HDR colour + depth, both at native screen resolution.
    var hdr   = g.Screen.Color("hdr",   Format.Rgba16F);
    var depth = g.Screen.Depth("depth", Format.Depth32F);

    // A per-frame uniform channel the scene pass reads (filled each frame via engine.Write).
    var viewProj = g.Constants<Matrix4x4>("camera.viewproj");

    // Scene pass: read the view-projection, write HDR colour + depth. The body only needs `scene` — a
    // stable producer, not a per-frame value — so pass it as pass state via the Draw<TState> overload:
    // a `static` lambda can then reach only `scene` and `ctx`, so a per-frame value can't accidentally
    // be captured and frozen at build time.
    g.Pass("scene")
        .Reads(viewProj)
        .Writes(hdr)
        .WritesDepth(depth)
        .Draw(scene, static (ctx, scene) =>
        {
            // Your own draw list, however you build it — scene.VisibleObjects is read live each frame.
            foreach (var obj in scene.VisibleObjects)
            {
                var call = DrawCall.Create(obj.Mesh, "scene");
                call.Material = obj.Material;          // fills the technique's reflected sampler slots
                // The per-draw block matching the shader. `ref` because the bytes alias this storage until the
                // draw is submitted — passing a temporary would leave the engine reading a dead local, so the
                // compiler rejects it rather than letting it corrupt at runtime.
                var model = obj.ModelMatrix;
                call.SetUniforms(ref model);
                ctx.Draw(call);
            }
        });

    // Tonemap pass: sample HDR, write the swapchain. `hdr` is a build-time resource handle (not
    // per-frame data), so capturing it directly is fine — only per-frame values need the state overload.
    g.Pass("tonemap")
        .Samples(hdr)
        .Writes(g.Backbuffer)
        .Draw(ctx => ctx.FullScreen("tonemap", hdr));

    // The terminal: present the swapchain. Acquire/present sync is derived behind this.
    g.Present(g.Backbuffer);
});

engine.SetGraph(graph);
```

The pass-builder verbs are the whole scheduling vocabulary — `.Writes` (produce a colour target), `.Blends` (accumulate onto existing contents), `.Samples` (bind as a shader-read input), `.WritesDepth` / `.ReadsDepth`, `.ReadWrites` (compute storage), `.ReadsVertex` (a vertex/instance stream), and `.Reads` (a constants or storage buffer). From these the engine derives execution order, every barrier, and every layout transition.

### 4. Render loop

`FrameInputs` carries only `DeltaSeconds` and `Time` (both `double`) — there is no engine `Camera` or `Scene`. View, light, and time data flow in through the neutral channels your passes read: write them by handle each frame *before* `Render`. Driven by Silk.NET's render callback:

```csharp
var elapsed = 0.0;   // double: an accumulator kept as float loses precision over a long session

window.Render += delta =>
{
    elapsed += delta;

    // Publish the matrix the "scene" pass reads. (BinaryVibrance.Kaldra.Concepts.Camera folds in the
    // clip-space correction so the same matrix is correct on both backends.)
    engine.Write(viewProj, camera.ViewProjection);

    engine.Render(new FrameInputs
    {
        DeltaSeconds = delta,    // double, seconds since the previous frame
        Time         = elapsed,  // double, seconds since startup
    });
};

window.Run();        // blocks, calling Render until the window closes
engine.WaitIdle();   // drain the GPU before teardown
```

Because the graph captures only the stable `viewProj` handle, `engine.Write` updates the bytes the shader reads without rebuilding the graph. The same immutable `RenderGraph` drives every frame; only a structural change (different passes, resources, or domains) triggers a rebake.

## Loading assets

`BinaryVibrance.Kaldra.Assets` provides neutral, graphics-API-free loaders that produce engine-boundary types (`TextureData`, `Vertex[]`). Hand those to the engine to mint stable `Texture`, `Mesh`, and `Material` handles, each living for the engine's lifetime.

**Images → textures → materials.** `ImageLoader.Load` decodes a PNG/JPG to `TextureData` (with a CPU-generated mip chain); pass `TextureFormat.Rgba8Srgb` for colour/albedo or `TextureFormat.Rgba8Unorm` for data such as normal maps. `CreateMaterial` bundles textures in the order the technique's shader binds them:

```csharp
using BinaryVibrance.Kaldra.Assets;
using BinaryVibrance.Kaldra.Core.Materials;

var albedoData = ImageLoader.Load("textures/albedo.png", TextureFormat.Rgba8Srgb);
var normalData = ImageLoader.Load("textures/normal.png", TextureFormat.Rgba8Unorm);

var albedo = engine.CreateTexture(albedoData);
var normal = engine.CreateTexture(normalData);

var material = engine.CreateMaterial(albedo, normal);   // texture 0 → sampler slot 0, …
```

**glTF → meshes.** `GltfLoader.Load` walks the node graph, bakes world transforms, and flattens the model to drawable primitives. Each `ModelPrimitive` carries `Vertices` (Kaldra's canonical position/normal/uv/tangent `Vertex`) and `Indices`, ready for `CreateMesh`:

```csharp
var model = GltfLoader.Load("models/character.glb");

foreach (var prim in model.Primitives)
{
    var mesh = engine.CreateMesh(prim.Vertices, prim.Indices);
    // … pair the mesh with a material and add it to your draw list …
}
```

For a rigged model, `model.Skeleton` and `model.Clips` carry the skeleton and animation clips, and a primitive's `SkinnedVertices` feed `engine.CreateSkinnedMesh(prim.SkinnedVertices, prim.Indices)` for the GPU pre-skin path (`ctx.DispatchSkin`). HDR environment maps load with `ImageLoader.LoadHdr`, which returns `Rgba16F` `TextureData`.
