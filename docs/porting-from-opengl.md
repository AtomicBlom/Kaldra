# Porting from OpenGL

How to move a game that draws with raw OpenGL onto Kaldra: what each GL habit becomes, the places where a direct
translation silently gives the wrong picture, and how to check the port frame by frame against the original.

The worked example throughout is [RubberTrack](https://github.com/AtomicBlom/RubberTrack), a procedural racing
game ported from OpenGL 3.3 (through Silk.NET) to Kaldra: a physics-driven band, a generated landscape, a banked road
with walls and bridges, a translucent sea, a 2D radar-chart overlay and a headless screenshot mode. Its GL renderers
were the usual kind — a shader wrapper over GLSL strings, a VBO re-uploaded every draw, each renderer setting its own
blend/depth state around its draws, one `RenderScene` calling them in order.

[`getting-started.md`](getting-started.md) is the API from scratch; this is the API from where a GL codebase starts.

---

## 1. The shape of the change

| OpenGL                                                  | Kaldra                                                                                                                                          |
|---------------------------------------------------------|-----------------------------------------------------------------------------------------------------------------------------------------------|
| You own the frame: clear, bind, set state, draw         | You declare a render graph once — passes, and what each reads and writes. Each pass's `.Draw` closure runs every frame.                        |
| A window with `Load` / `Update` / `Render` events       | A `KaldraApplication`: `Configure`, `LoadContent`, `FixedUpdate`, `Update`, `BeforeRender`, `UnloadContent`. `KaldraApplication.Run(app, args)`.   |
| Input event handlers                                    | Polling `Input` (an `InputSnapshot`) in `Update`                                                                                               |
| Your own fixed-step accumulator                         | `AppOptions.FixedStepHz` and `FixedUpdate`                                                                                                     |
| GLSL strings compiled at run time                       | Slang files compiled at build time to SPIR-V and signed DXIL; a *technique* names one                                                          |
| `glUniform*`                                            | Per-frame data: a `g.Constants<T>` block written with `Engine.Write`. Per-draw data: `DrawCall.SetUniforms`.                                   |
| `glEnable(GL_BLEND)`, `glDepthFunc`, `glCullFace`, `glPolygonOffset` | Fixed per technique: `TechniqueDescriptor.Blend`, `DepthTest`, `Cull`, `Bias`                                                     |
| `glDepthMask`, `glDisable(GL_DEPTH_TEST)`               | Per pass: `.WritesDepth`, `.ReadsDepth`, or no depth target                                                                                    |
| `glClearColor`                                          | A clear value fixed when the graph is built; a colour that changes is drawn                                                                    |
| `GL_LINES`, `GL_TRIANGLE_STRIP`, `GL_TRIANGLE_FAN`      | Triangle lists only                                                                                                                           |
| `glBufferData` every frame                              | CPU instances (`DrawCall.From`), uploaded by the engine each frame without allocating                                                          |
| `glDeleteBuffers`                                       | `Engine.ReleaseMesh(mesh)`                                                                                                                     |
| `glDeleteTextures`                                      | `Engine.ReleaseTexture(texture)` — not for a texture a material holds                                                                          |
| An FBO in a hidden window, read back with `glReadPixels` | An engine on `Surface.Offscreen(w, h)`, and `Engine.RequestCapture(path)`                                                                     |

The rest of this guide goes through those rows, then the two things a port gets wrong without any error: **colour**
(§5) and **depth** (§9).

---

## 2. Setting up

With the `BinaryVibrance.Kaldra.Sdk` package, follow [`getting-started.md`](getting-started.md). With Kaldra as a **git submodule** inside
your game's directory, which is the easier way to move engine and game together during a port, keep your project's
own SDK and import Kaldra's by hand:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <!-- Before Sdk.props: it decides from these which shader targets to compile -->
  <PropertyGroup>
    <UseVulkan>true</UseVulkan>
    <UseDirectX12 Condition="$([MSBuild]::IsOSPlatform('Windows'))">true</UseDirectX12>
    <UseVysara>true</UseVysara>
  </PropertyGroup>

  <Import Project="Kaldra/build/BinaryVibrance.Kaldra.Sdk/Sdk/Sdk.props" />

  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net11.0</TargetFramework>
    <KaldraShaderSourceDir>$(MSBuildProjectDirectory)\Shaders</KaldraShaderSourceDir>
    <!-- The submodule sits inside the project directory; keep its sources out of the game's compile -->
    <DefaultItemExcludes>$(DefaultItemExcludes);Kaldra/**</DefaultItemExcludes>
  </PropertyGroup>

  <ItemGroup>
    <!-- Signs the DXIL the DirectX 12 backend loads; build machine only -->
    <PackageReference Include="Microsoft.Direct3D.DXC" Version="1.9.2609.5" GeneratePathProperty="true" PrivateAssets="all" ExcludeAssets="all" />
    <!-- Camera and other app-side helpers are not brought in by the SDK -->
    <ProjectReference Include="Kaldra/BinaryVibrance.Kaldra.Concepts/BinaryVibrance.Kaldra.Concepts.csproj" />
  </ItemGroup>

  <Import Project="Kaldra/build/BinaryVibrance.Kaldra.Sdk/Sdk/Sdk.targets" />

</Project>
```

Three things that are easy to miss:

- **`DefaultItemExcludes`.** Without it, a submodule checked out under the project directory is globbed into the
  game's own compile — thousands of engine sources.
- **The `Use*` flags go before `Sdk.props`.** It derives which shader targets to emit from them, eagerly.
- **The submodule has a submodule** (`external/VMASharp`): `git submodule update --init --recursive`. Vysara, for an
  app with `UseVysara`, is a checkout of its own beside the engine's (`..\Vysara` from it, or `VysaraSource`).

Remove the Silk.NET OpenGL, windowing and input packages; the desktop host brings its own.

---

## 3. The application

The window, the device and the frame loop belong to Kaldra's host. The game is a `KaldraApplication`, and `Program.cs`
is one line:

```csharp
KaldraApplication.Run(new Game(), args);
```

| GL-era code                                   | Where it goes                                                                                              |
|-----------------------------------------------|------------------------------------------------------------------------------------------------------------|
| Window size and title                         | `Configure(AppOptions options)`                                                                            |
| `OnLoad`: compile shaders, create buffers     | `LoadContent`: `Engine.DefineTechnique`, `Engine.CreateMesh`, build and `SetGraph` the render graph       |
| A fixed-step physics accumulator              | `options.FixedStepHz = 60` and `FixedUpdate(FixedTime)`, which has the same five-step catch-up guard      |
| `OnUpdate`                                    | `Update(FrameTime)`                                                                                        |
| `OnRender`                                    | `BeforeRender(FrameTime)` prepares the frame (§4); the engine renders it                                   |
| Framebuffer-resize handler                    | `OnResize(width, height)`, and `SurfaceSize`                                                               |
| `window.Close()`                              | `Exit()`                                                                                                   |
| `IKeyboard.KeyDown += …`                      | `if (Input.Pressed(Key.Space)) …` in `Update`                                                              |
| Mouse drag and wheel handlers                 | `Input.Down(MouseButton.Left)` with `Input.PointerDelta`; `Input.Wheel` (a per-frame delta)                |

Pick a fixed-step rate whose reciprocal *is* your step (`FixedStepHz = 60` with `const double Step = 1.0 / 60`). A
rate computed as `1 / (1.0 / 60)` can come out an ulp off, and a driver ticking at exactly the step then runs zero
steps one frame and two the next.

The desktop host parses its own launch options (`--backend vulkan|dx12`, `--no-vsync`, …) and hands the rest to
the app as `AppArgs`. `HostOptions.Parse(args)` (in `BinaryVibrance.Kaldra.Hosting.Desktop`) is public, for a `Program.cs` that wants
to act on its own flags before the host starts — RubberTrack's `--capture` does (§11).

---

## 4. A frame

GL's frame is one function that clears, then has each renderer upload its geometry, set its uniforms and state, and
draw. In Kaldra it comes apart into three:

1. **The graph, declared once.** Passes in order, with what each reads and writes. The engine derives everything
   else — barriers, layout transitions, which targets can share memory. The order of draws inside a pass is the
   order your closure issues them, so the old `RenderScene` order survives inside each pass.
2. **Per-frame preparation**, in `BeforeRender`: compute the camera, `Engine.Write` the per-frame constants, and fill
   each renderer's instance arrays and uniform blocks. This is where the parameter lists of the old `Draw(...)`
   calls go.
3. **Recording**, in each pass's closure: `renderer.Record(ctx)` issues `ctx.Draw` calls from what step 2 left.

```csharp
private RenderGraph BuildGraph() => RenderGraph.Define(g =>
{
    var screen = g.Backbuffer;
    var scene = g.Screen.Color("scene", Format.Rgba8Unorm);              // §5
    var depth = g.Screen.Depth("depth", Format.Depth32F, clearDepth: 0f); // §9
    var frame = _frame = g.Constants<FrameData>("frame");

    g.Pass("sky").Writes(scene)
        .Draw(this, static (ctx, game) => ctx.FullScreen("sky", game._skyColour));
    g.Pass("world").Reads(frame).Blends(scene).WritesDepth(depth)
        .Draw(this, static (ctx, game) => game.RecordWorld(ctx));
    g.Pass("lines").Reads(frame).Blends(scene).ReadsDepth(depth)
        .Draw(this, static (ctx, game) => game.RecordLines(ctx));
    g.Pass("sea").Reads(frame).Blends(scene).ReadsDepth(depth)
        .Draw(this, static (ctx, game) => game._terrain.RecordSea(ctx, game._quad));
    g.Pass("overlay").Reads(frame).Blends(scene)
        .Draw(this, static (ctx, game) => game._radar.Record(ctx, game._quad));
    g.Pass("display").Samples(scene).Writes(screen)
        .Draw(scene, static (ctx, scene) => ctx.FullScreen("display", scene));
    g.Pass("ui").Blends(screen)
        .Draw(this, static (ctx, game) => game._labels.Record(ctx));
    g.Present(screen);
});
```

Write the closures as `static` lambdas over a stable object (`Draw(this, static (ctx, game) => …)`). The graph is
built once; a closure that captured a per-frame *value* would freeze it, and a `static` lambda can't. Reading the
game's *live* state through the object is the intended pattern.

Renderers change shape accordingly. A GL renderer owned GL objects and drew immediately. A Kaldra renderer defines its
techniques in its constructor, fills arrays in `Prepare(...)`, and draws in `Record(ctx)`.

---

## 5. Colour: the backbuffer is sRGB

**This is the trap.** GL's default framebuffer shows shader output as it is. Kaldra's backbuffer is sRGB on every
backend (Vulkan `B8G8R8A8_SRGB`, an `_SRGB` view on DirectX 12): it treats shader output as *linear light* and
gamma-encodes it. Every colour a GL game chose by eye comes out brighter and washed out. RubberTrack's dark void
(0.08) arrived at 79/255 instead of 20, and its first frames differed from GL's in every pixel by ~45/255 on average.

There are two ways to keep the GL look:

**Decode in every fragment shader** — sRGB → linear on the final colour, so the backbuffer's encode puts it back.
Opaque pixels then match GL exactly, but blending now happens in linear light, and translucent colours tuned in
gamma space move a long way. RubberTrack's radar panel, dark navy at 70% opacity, came out mid-grey.

**Compose in gamma space and decode once** — what RubberTrack does. Every pass draws into an `Rgba8Unorm` target, so
values *and* blending are exactly GL's, 8-bit quantisation included. A final fullscreen pass decodes it onto the
backbuffer, whose encode returns the same 8-bit values:

```hlsl
#include "kaldra_platform.module.slang"

Sampler2D scene;

struct VsOut { float4 position : SV_Position; float2 uv : TEXCOORD0; };

[shader("vertex")]
VsOut vertexMain(uint vertexId : SV_VertexID)
{
    float2 uv = float2((vertexId << 1) & 2, vertexId & 2);
    VsOut output;
    output.position = KALDRA_FULLSCREEN_POS(uv);
    output.uv = uv;
    return output;
}

[shader("fragment")]
float4 fragmentMain(VsOut input) : SV_Target
{
    float3 c = scene.Sample(input.uv).rgb;
    return float4(lerp(pow((c + 0.055) / 1.055, 2.4), c / 12.92, step(c, 0.04045)), 1.0);   // sRGB → linear
}
```

It costs one fullscreen pass and one 8-bit target. Anything designed for the sRGB backbuffer — Vysara or ImGui UI —
draws after the display pass, onto the backbuffer itself.

The third option is re-grading every colour for linear light, which is a design change rather than a port.

---

## 6. Shaders

GLSL becomes Slang (HLSL-flavoured). The full contract is [`shader-authoring.md`](shader-authoring.md); what a GL
port runs into:

- **Start every shader with `#include "kaldra_platform.module.slang"`.**
- **Uniforms become constant buffers, filled in a fixed order:** the pass's `.Reads(...)` blocks first, then the
  draw's per-draw block. In the shader they take no binding or register, only their order:

  ```hlsl
  ConstantBuffer<Frame> frame;       // the pass .Reads(frame)
  ConstantBuffer<Terrain> terrain;   // DrawCall.SetUniforms(ref block)
  ```

  Where GL had `layout(binding = n)`, leave it out: Slang numbers each target in declaration order.
- **Lay blocks out in `float4` rows**, and give the C# struct the same rows (`Vector4` fields,
  `[StructLayout(LayoutKind.Sequential)]`). HLSL and WGSL disagree about tighter packing.
- **Vertex inputs** carry both a Vulkan location and a semantic, matching the `VertexLayout` the mesh was uploaded
  with:

  ```hlsl
  struct VsIn
  {
      [[vk::location(0)]] float3 position : POSITION;
      [[vk::location(1)]] float3 normal : NORMAL;
      [[vk::location(2)]] float3 colour : COLOR0;
      [[vk::location(3)]] float2 rise : TEXCOORD0;
  };
  ```

  A per-instance stream (§7) continues the locations after the mesh's.
- **Matrices.** A System.Numerics `Matrix4x4` (row-vector, `view * projection`) uploaded as it is reads as its
  transpose in a constant buffer, so shaders write `mul(frame.viewProj, float4(p, 1.0))`.
- **Small things that cost a build:** `line` is an HLSL keyword (a geometry-shader input), so it can't name a
  variable; don't shadow an intrinsic such as `length` with a local; `mix` is `lerp`, `vec3` is `float3`,
  `fract` is `frac`.

`TechniqueDescriptor` then states what GL set as state at draw time:

```csharp
engine.DefineTechnique("terrain", new TechniqueDescriptor("terrain",
    HasVertexInput: true, Layout: LitVertex.Layout, DepthTest: DepthCompare.GreaterOrEqual));
engine.DefineTechnique("sea", new TechniqueDescriptor("sea",
    HasVertexInput: true, Layout: UnitQuad.Layout, Blend: BlendMode.AlphaOver, DepthTest: DepthCompare.GreaterOrEqual));
```

One shader with two sets of state is two techniques.

---

## 7. Geometry

### Meshes that are uploaded once

`glVertexAttribPointer` layouts become a `VertexLayout` of semantics, and the vertex data a matching struct:

```csharp
[StructLayout(LayoutKind.Sequential)]
struct LitVertex(Vector3 position, Vector3 normal, Vector3 colour, Vector2 animation)
{
    public Vector3 Position = position;
    public Vector3 Normal = normal;
    public Vector3 Colour = colour;
    public Vector2 Animation = animation;

    public static readonly VertexLayout Layout = new(
        new VertexAttribute("POSITION", VertexFormat.Float3),
        new VertexAttribute("NORMAL", VertexFormat.Float3),
        new VertexAttribute("COLOR", VertexFormat.Float3),
        new VertexAttribute("TEXCOORD", VertexFormat.Float2));
}

var ground = Engine.CreateMesh<LitVertex>(vertices, LitVertex.Layout, indices);
```

`Engine.ReleaseMesh(ground)` frees it when the game moves on — a new level, a regenerated world. The handle dies at
once (drawing it throws, naming the release), and the buffers are freed once every frame that might still read them
has finished, so it is safe to call right after the last frame that drew the mesh, like `glDeleteBuffers`.

`CreateMesh` belongs to the render thread; from any other thread, `CreateMeshAsync` queues the upload for the next
frame. RubberTrack builds its terrain's vertex arrays on a worker, then uploads them from `Update`.

### Geometry that changes every frame

There is no mutable mesh. A GL renderer that rebuilt a VBO every frame with `glBufferData` has three options:

- **CPU instances** — the general answer. Upload a tiny mesh once, keep an array of per-instance structs, refill it
  each frame and draw with `Instances = DrawCall.From<T>(array.AsSpan(0, count))`. The engine copies the span into
  a per-frame ring; nothing allocates. The instance stream is a `VertexLayout` with `VertexInputRate.PerInstance`
  at binding 1, named as the technique's `Instance`.
- **A storage buffer** (`g.Storage<T>` written with `Engine.Write`), for data a shader indexes rather than streams.
- **`ctx.DrawList`**, the UI path: per-frame vertices with 16-bit indices and a texture per command.

A draw with no mesh, whose vertices the shader makes up from `SV_VertexID`, works only with a GPU-filled instance
buffer; CPU instances need a mesh to instance. RubberTrack's answer is one mesh nearly everything is drawn with: a
**unit quad**, corners (0,0) to (1,1), which each vertex shader stretches over whatever its instance describes.

```csharp
static readonly VertexLayout QuadLayout = new(new VertexAttribute("TEXCOORD", VertexFormat.Float2));
var quad = Engine.CreateMesh<Vector2>([new(0, 0), new(1, 0), new(0, 1), new(1, 1)], QuadLayout, [0, 1, 2, 2, 1, 3]);
```

### Lines, strips and fans

Kaldra draws triangle lists only. A technique has no primitive topology.

- **Lines** become one quad instance per segment, widened in the vertex shader. Project both ends, find the
  segment's direction on screen, push each corner out across it by half the width in pixels (plus one, for an
  anti-aliased edge the fragment shader fades), and past each end by the same, so consecutive segments overlap at
  their joins. Lines gain a width and anti-aliasing `GL_LINES` never had.

  **Clip against the eye yourself.** GL clipped a line that crossed behind the camera before the perspective divide.
  A screen-space expansion divides by `w`, so cut the segment to just in front of the eye first:

  ```hlsl
  if (c0.w < NearW && c1.w < NearW) { output.position = float4(2, 2, 2, 1); return output; }   // wholly behind
  if (c0.w < NearW)      c0 = lerp(c0, c1, (NearW - c0.w) / (c1.w - c0.w));
  else if (c1.w < NearW) c1 = lerp(c1, c0, (NearW - c1.w) / (c0.w - c1.w));
  ```

  Without it, a segment through the camera turns into a quad across the whole screen.
- **Strips** (a road surface, a wall) become one instance per segment, carrying both of its ends; the shader
  stretches the quad between them.
- **Fans** become one instance per triangle, drawn over only the quad's first triangle
  (`Indices = DrawCall.Range(0, 3)`), whose corners (0,0), (1,0), (0,1) pick the triangle's three points.

---

## 8. Fixed-function state

| GL                                          | Kaldra                                                                                                                                  |
|---------------------------------------------|---------------------------------------------------------------------------------------------------------------------------------------|
| `glBlendFunc(SRC_ALPHA, ONE_MINUS_SRC_ALPHA)` | `Blend: BlendMode.AlphaOver` on the technique, in a pass that `.Blends` its target                                                   |
| `glDepthFunc`                               | `DepthTest:` on the technique                                                                                                          |
| `glDepthMask(GL_FALSE)`                     | Draw in a pass that `.ReadsDepth(depth)` rather than `.WritesDepth(depth)`                                                             |
| `glDisable(GL_DEPTH_TEST)`                  | Draw in a pass with no depth target                                                                                                    |
| `glPolygonOffset(factor, units)`            | `Bias: new DepthBias(Constant: units, SlopeScale: factor)` on the technique — mind the sign under reversed-Z (§9)                      |
| `glCullFace`                                | `Cull:` on the technique. The front face is counter-clockwise, as GL's default.                                                        |
| `glClearColor` that changes                 | A fullscreen pass: a technique with `HasFragmentPush: true` and `ctx.FullScreen("sky", colour)`                                       |
| `glViewport` to the window                  | Nothing: a pass covers its target                                                                                                     |

**Depth write is per pass, not per draw.** That reaches further than translucent geometry. Wide screen-space lines
take their depth from their centreline, so two of them lying on the same surface fight at every crossing: in
RubberTrack the grid notched the band wherever they crossed, which GL's 1-pixel lines never showed. A `lines` pass
that tests depth without writing it fixed that.

---

## 9. Depth and the camera

GL's clip-space depth is `[-1, 1]`; Kaldra's is `[0, 1]`. A hand-built GL perspective matrix is wrong as it stands.
Use `BinaryVibrance.Kaldra.Concepts.Camera`, which builds a right-handed `[0, 1]` projection and composes the engine's clip-space
correction:

```csharp
_camera = new Camera(Engine);                                               // once
var viewProj = _camera.Perspective(fov, aspect, near, far, reversed: true)  // each frame
                      .LookAt(eye, target, up)
                      .ViewProjection;
Engine.Write(_frame, new FrameData { ViewProj = viewProj, … });
```

**Consider reversed-Z while you are there.** A GL game's 24-bit depth buffer often needed polygon offset and a careful
near plane. With `reversed: true`, a depth target cleared to 0 (`Depth(name, Format.Depth32F, clearDepth: 0f)`) and
`DepthTest: DepthCompare.GreaterOrEqual` on every technique, a 32-bit float buffer holds precision out to the far
plane. All three pieces must agree, or occlusion inverts without an error. The sign of depth bias flips with it:
nearer is *greater*, so GL's `glPolygonOffset(-1, -2)` becomes `DepthBias(Constant: 2, SlopeScale: 1)`.

**Better precision will expose things GL hid.** RubberTrack drew its band 0.3 m above the road it turns into; with
GL's 24-bit depth and polygon offset the road won anyway, unevenly, and the band seemed to disappear into it. Kaldra
honoured the lift, and the band ran as a yellow line round the whole finished track. Compare frames against the GL
build, not just against what the code says (§12), and expect to make a few such accidents deliberate.

---

## 10. 2D overlays and text

On Vulkan and DirectX 12, clip space is **y-down** (DirectX 12 through a negative-height viewport): y = -1 is the
top of the screen, so GL-style overlay coordinates (NDC, y up) come out upside down. WebGPU's clip space is y-up, so
no one hand-written convention is right on all three. Lay overlays out in pixels from the top-left, the space Vysara
lays out in, and always go through the platform macros, which flip y where the backend needs it:
`KALDRA_OVERLAY_POS(pixel, 2.0 / viewport, float2(-1, -1))` for pixels, `KALDRA_CLIP_POS(ndc)` for a position already in
y-down clip space, and `KALDRA_FULLSCREEN_POS(uv)` for a fullscreen triangle (all in `kaldra_platform.module.slang`).
Overlay and UI then line up without any conversion between them, on every backend.

A GL game without text can have it now. Vysara, drawn by `BinaryVibrance.Kaldra.UI.Vysara`'s `UIPainter` in a pass of its own, sets
labels on a `Canvas` of `TextBlock`s in a few lines:

```csharp
var host = new UIHost();
var view = host.CreateRoot(UILayer.Default, interactive: false);
view.Root.Content = canvas;   // a Canvas of TextBlocks, placed with Canvas.SetLeft / SetTop
var painter = UIPainter.Create(Engine, host);
// each frame, in BeforeRender: host.Tick(dt, time, SurfaceSize); host.Render(); painter.Prepare();
// and in the pass: painter.Draw(ctx, view.Surface)
```

Text is wider than the icons a GL HUD used in its place. Around a small chart, anchor each label by its corner or
edge nearest the chart rather than centring it on its direction, or neighbours run together.

---

## 11. Screenshots and headless runs

The desktop host always opens a window: it has no hidden or headless mode. To render without one, bring an engine up
on an offscreen surface and drive your `KaldraApplication` yourself, the way the host would:

```csharp
using var engine = backend.Create(Surface.Offscreen(1280, 720), 1280, 720, new EngineSettings { EnableValidation = false });
var app = (IHostedApplication)game;
var control = new Control();          // an IHostControl that records RequestExit
app.ReadOptions();
app.Load(engine, control);
app.Resize(1280, 720);
for (var total = 0.0; !control.ExitRequested; total += Step)
    app.Tick(new FrameTime(Step, total));
app.Unload();
engine.WaitIdle();
```

`Backends.Available` lists what the SDK registered; pick one by `Name` to capture on a particular backend. Then
`Engine.RequestCapture(path)` in `BeforeRender` writes that frame as a PNG; a hand-written PNG encoder can go.

GL code could render several views inside one frame and read each back at once. Here a capture is one whole frame.
When a moment calls for several views, queue them, and let the game stand still (skip `FixedUpdate` and `Update`
while the queue is not empty) as each queued shot poses the camera for its own frame.

---

## 12. Checking the port

- **Capture both builds.** Check the pre-port commit out into a git worktree and run its capture mode next to the new
  one. Anything deterministic from a seed — a simulation, a generated world — gives frame-for-frame pairs.
- **Numbers first.** `Scripts/Verification/colorcount.ps1` catches an empty frame; `diff.ps1` gives the mean and
  maximum difference, the fraction of pixels over a threshold, and a heatmap. RubberTrack went from ~45/255 mean
  (the sRGB trap) to ~0.15/255, with what remained exactly where the port meant to differ.
- **Then look**, at the pairs and the heatmaps, against a written list of the differences you expect. The numbers
  say how much; only looking says whether. RubberTrack's first look found three real problems — linear blending of
  translucent colours, lines fighting on a shared surface, and the band — that the numbers had not explained.
- **Diff the backends against each other** too: the same capture on Vulkan and DirectX 12 should agree within
  `diff.ps1`'s parity bar.

---

## 13. Where Kaldra is thin for a port

Worth knowing before you start, each with the workaround used above:

- **No per-frame clear colour** — draw the background with a fullscreen pass.
- **No per-draw depth mask** — move the draws to a pass of their own that `.ReadsDepth`.
- **No mesh-less draw from CPU instances** — instance a unit quad.
- **No primitive topology** — lines, strips and fans become triangle instances.
- **No headless desktop host** — drive the app on an offscreen engine.
