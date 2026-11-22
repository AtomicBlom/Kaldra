# Loom API Vocabulary

A reference for every key type in Loom's public API — *how to use it*. You author against `Loom.Core` (the Describe layer); the engine derives ordering, barriers, layout transitions, and aliasing for you. Verbs read as intent — you state what a pass reads and writes, never the order.

## Graph description (`Loom.Core.Graph`)

A graph is built once with `RenderGraph.Define(g => { … })` and set on the engine with `engine.SetGraph`. The closure receives a `GraphBuilder` (`g`); you declare domains, resources, and passes on it.

| Type / member | Role |
|---|---|
| `RenderGraph.Define(g => …)` | The one entry point: runs the authoring closure and returns an immutable, hashable `RenderGraph`. Hand it to `engine.SetGraph`. |
| `g.Pass("name")` | Begin a raster pass → `PassBuilder`. Chain access verbs, then `.Draw(ctx => …)`. |
| `g.Compute("name")` | Begin a compute pass → `PassBuilder`. Chain access verbs, then `.Dispatch(ctx => …)`. |
| `g.Present(colorHandle)` | The terminal: declare which colour resource is shown this frame (returns void). |
| `g.Readback(handle)` | Declare a GPU→CPU readback of a resource; returns the same handle. Read it later with `engine.Read`. |
| `using (g.Scope("name"))` | Prefix every domain/resource/pass declared in the block with `name.`, so a reusable fragment can be instantiated more than once. Nests. Techniques and the backbuffer are not scoped. |
| `g.Buffer<T>("name", count)` | A GPU storage buffer of `count × sizeof(T)` (not resolution-bound) → `BufferHandle`. |
| `g.Buffer("name", count, layout)` | The same, sized by a `VertexLayout`'s stride — the spelling for a buffer a compute fills and a draw consumes as its per-instance stream. Naming the layout the technique names lets the draw path reject a stride mismatch. |
| `g.Buffer("name", byteSize)` | The same as an opaque block of `byteSize` bytes. Declares no element contract, so neither draw-path guard applies — prefer one of the two above. |
| `g.Constants<T>("name")` | An app-written uniform/constant block of `sizeof(T)` bytes → `ConstantsHandle`. Fill with `engine.Write`. |
| `g.Storage<T>("name", count, growable=false)` | An app-written structured array (SSBO), `count × sizeof(T)` → `StorageBufferHandle`. `growable: true` lets a larger `Write` grow it. A `Constants` or `Storage` buffer is uploaded only into the frame slots behind its last `Write`, so one written once costs nothing per frame however large it is; the last `Write` persists either way. |
| `g.Persistent("name", format, size=1)` | A screen-sized cross-frame colour image whose memory is never aliased → `ColorHandle`. Only `size == 1` is supported. A window resize reallocates it at the new extent, so its contents are lost (there is no resample) — use `domain.Persistent` on a fixed-size domain for history that must outlive a resize. |

### Pass access declarations (`PassBuilder`, all chainable)

You declare intent; the engine derives ordering, barriers, and layout transitions.

| Verb | Meaning |
|---|---|
| `.Writes(params ColorHandle[])` | Produce colour target(s) (the engine may clear). Multiple handles = MRT. |
| `.Blends(ColorHandle)` | Read-modify-write: load existing contents + blend onto them (the pass is last reader and next writer). |
| `.Samples(params ITextureHandle[])` | Bind colour/depth image(s) as shader-read (sampled) inputs. |
| `.WritesDepth(DepthHandle)` / `.ReadsDepth(DepthHandle)` | Depth write / depth test without write. |
| `.ReadWrites(params IStorageHandle[])` | Read-modify-write storage images or buffers (compute UAV). |
| `.ReadsVertex(BufferHandle)` | Bind a buffer as a vertex / instance stream (a compute pass filled it). |
| `.ReadsIndirect(BufferHandle)` | Bind a buffer as draw arguments — the counts the command processor reads instead of the CPU supplying them. |
| `.Reads(IUniformHandle)` | Read a `g.Constants` block as the technique's uniform input, in a raster pass or a compute pass alike: a technique's constant buffers take the pass's blocks in the order read, and a compute technique declaring more than its pass reads throws at dispatch. |
| `.Reads(IStorageBufferHandle)` | Read a `g.Storage` SSBO read-only (an overload of `.Reads`). Bound to every draw in the pass, mesh or fullscreen, **by position**: a technique's first storage buffer takes the first the pass reads, its second the second, and so on, as many as it declares (vertex or fragment stage) — never by name. So a technique that reads only some of a pass's storage buffers must declare them in the order the pass reads them, from its first: one that declares only the pass's second gets its first. Where the element sizes show it (a slot's reflected element against the buffer's `sizeof(T)`, packed or padded to its std430 alignment), that throws at the draw, naming both; one declaring more than the pass reads throws too. A material draw whose technique reads storage throws, on every backend. In a compute pass the technique's read-only storage buffers (`StructuredBuffer<T>`) take them by the same rule, and its read-write ones the dispatch's own storage; one declaring more read-only buffers than its pass reads, or more read-write ones than the dispatch supplies, throws at dispatch. A read-only slot binds only the pass's `.Reads` storage, so a GPU-produced `g.Buffer` a dispatch supplies must be declared `RWStructuredBuffer` in the shader even when it is only read. |
| `.Queue(Queue)` | Assign the pass to a queue (see `Queue` below). |
| `.Draw(Action<IRenderContext>)` / `.Dispatch(Action<IRenderContext>)` | The per-frame record callback (raster / compute). Pull from `ctx`; never capture per-frame data. |

### Special handles & domains

`Domain`s decide where each resource lives. Declare a colour/depth target *on* a domain — there is no standalone `g.Color`.

| Member | Description |
|---|---|
| `g.Backbuffer` | The swapchain colour target (`ColorHandle`) — *is* the present surface. Hand it to `.Writes`/`.Blends` and `g.Present`. |
| `g.Screen` | The native-resolution `Domain` (alias for `g.Backbuffer.Domain`); declare full-res sibling targets here. |
| `g.Domain("name", width, height, samples=1)` | A fixed-size `Domain` (a constant-size pixel-art world); `samples` is the MSAA count (power of two, within `Capabilities.SampleCounts`: WebGPU has 1 and 4). Every target in a multisampled domain is multisampled, and so is every pipeline drawing into it. A pass that `.Samples` a multisampled colour target reads its *resolve*: the engine resolves it into a single-sample image of its own wherever the graph takes it from being drawn to being sampled, so nothing in the graph names the resolve. Anything else is refused when the graph is derived, the same on every backend: sampling a multisampled depth target, `.ReadWrites` on a multisampled target (a storage image is single-sample), `g.Readback` or `g.Present` of one, and a `Persistent` target in a multisampled domain. Group-opacity UI layers drawn in a multisampled pass render single-sample and composite back at the pass's count. |
| `g.Domain("name", scale, samples=1)` | A backbuffer-relative `Domain` — `scale × native`, re-resolved on resize (e.g. `0.5f` for half-res). |
| `domain.Color("name", format, clearValue?=null, noClear?=false)` | A colour target in this domain → `ColorHandle`. `noClear: true` skips the clear (only when the first pass covers every pixel). |
| `domain.Color("name", format, scale, …)` | A colour target at a fraction of the domain's resolution (mip chains, bloom pyramid). |
| `domain.Depth("name", format, clearDepth = 1f, noClear = false)` | A depth target in this domain → `DepthHandle`. `clearDepth: 0f` is the reversed-Z far plane; `noClear` skips the clear (only correct when the first depth-writing pass covers every pixel). |
| `domain.Persistent("name", format)` | A cross-frame target sized to this domain (use a tiny domain for a scalar history). On a fixed-size domain its contents survive a window resize; on a backbuffer-relative one it is reallocated like any other screen-sized target. |
| `g.BackbufferClear` / `g.BackbufferNoClear` | Set the backbuffer's first-write clear colour / skip the clear — before the first `g.Backbuffer` access. |

So a target is declared `g.Screen.Color("hdr", Format.Rgba16F)` or `g.Screen.Depth("depth", Format.Depth32F)`.

### `Format` (render-graph attachment formats, `Loom.Core.Graph`)

The complete set of attachment/target formats:

| Format | Typical use |
|---|---|
| `Rgba8Srgb` | sRGB colour the eye reads directly (the default backbuffer encoding). |
| `Rgba8Unorm` | 8-bit RGBA linear data (packed material params). |
| `Bgra8Srgb` | The channel-swapped sibling of `Rgba8Srgb` — the backbuffer takes it where the platform's preferred swapchain or canvas format is BGRA (most WebGPU canvases). |
| `Rgb10A2` | 10/10/10/2 packed — cheap higher-precision normals. |
| `Rgba16F` | HDR colour with range for bloom. |
| `R16F` | Single-channel float — SSAO, the exposure scalar, small object-id picking. |
| `R32F` | Single-channel full-precision float — the only single-channel float WebGPU allows as a read-write storage texture (the auto-exposure scalar). |
| `R32Uint` | Single-channel 32-bit uint — exact integer object ids of any range. |
| `Rg16F` | Two-channel float — velocity, JFA seed coordinates. |
| `Depth32F` | 32-bit float depth. |

`format.BytesPerTexel` and `format.IsFilterableFloat` (`FormatExtensions`) answer the two questions every backend needs the same answer to: a readback's texel size, and whether a shader can read the format through a filtering float sampler on every backend (false for `Depth32F`, `R32Uint` and `R32F`).

### `Queue` (`Loom.Core.Graph`)

`Queue.Graphics` (the default for every pass) and `Queue.AsyncCompute`. `AsyncCompute` is reserved for a future multi-queue scheduler — assigning it currently fails loudly at derive time rather than silently running on the graphics queue.

## Authoring a graph

```csharp
var graph = RenderGraph.Define(g =>
{
    var screen   = g.Backbuffer;                              // the swapchain target
    var depth    = g.Screen.Depth("depth", Format.Depth32F);
    var viewProj = g.Constants<Matrix4x4>("camera.viewproj"); // a per-frame uniform channel

    g.Pass("opaque")
        .Reads(viewProj)             // declared inputs/outputs — ordering & barriers are derived
        .Writes(screen)
        .WritesDepth(depth)
        .Draw(ctx =>
        {
            foreach (var obj in visibleObjects)        // your own culled draw list, however you build it
            {
                var call = DrawCall.Create(obj.Mesh, "lit");
                call.Material = obj.Material;
                var model = obj.ModelMatrix;
                call.SetUniforms(ref model);           // a per-draw block, matching the shader's cbuffer
                ctx.Draw(call);
            }
        });

    g.Present(screen);
});
engine.SetGraph(graph);
```

Ping-pong (bloom, JFA) is just two handles and a `(read, write)` tuple swapped in a normal loop — the graph follows the handles. View, light, and time data flow in through `g.Constants`/`g.Storage` channels a pass `.Reads`; the engine names no game concept.

## Engine hosting (`Loom.Core.Hosting`)

| Type | Role |
|---|---|
| `Backends` | Static backend registry. `Backends.Available` is an `IReadOnlyList<IRenderBackend>` (registration order); each referenced backend assembly self-registers as it loads. |
| `IRenderBackend` | A discoverable backend: `string Name` and `IEngine Create(object surface, int width, int height, EngineSettings settings)`. The surface is opaque (Vulkan `IVkSurface` / DX12 `HWND`). |
| `IEngine` | The live engine — owns the device and pipeline cache, survives graph swaps. See its members below. |
| `IRenderContext` | The per-frame execution context passed to `.Draw` / `.Dispatch`. Closures *pull* from it rather than capturing. |
| `EngineSettings` | Backend-neutral engine config (subclass per backend). |
| `FrameInputs` | The per-frame host data handed to `IEngine.Render`. |
| `TechniqueDescriptor` | A backend-neutral technique definition (`Loom.Core.Materials`), registered with `IEngine.DefineTechnique`. |

### Selecting a backend and creating the engine

```csharp
var backend = Backends.Available.FirstOrDefault(b => b.Name == settings.PreferredBackend)
              ?? Backends.Available.First();
using var engine = backend.Create(window.Surface, width, height, settings);
```

`Backends.Available` is a list, not a dictionary — select by `Name`, never an indexer. Constructing a backend's `EngineSettings` subclass (`VulkanEngineSettings` / `DirectX12EngineSettings`) is what loads and registers that backend.

### Input on the desktop (`Loom.Hosting.Desktop`)

The host turns the window's devices into `Loom.Input`'s neutral `InputEvent` stream: keyboards and mice through
GLFW (`DesktopInputPump`), gamepads through SDL 3 (`SdlGamepadPump`), or through GLFW where SDL is missing or won't start.

| Member | Purpose |
|---|---|
| `HostOptions.Pads` (`--pads sdl\|glfw`) | Where gamepads are read from: `GamepadSource.Sdl` (default) or `GamepadSource.Glfw`. |
| `SdlGamepadPump` | SDL's gamepad subsystem alone (no SDL window), pumped once a frame on the host's thread: SDL's own drivers where it has them (DualSense and DualShock over HID, with rumble; Switch pads; the Steam Controller without Steam), XInput and Windows.Gaming.Input pads otherwise. Pads keep the lowest free of the four slots while connected. The host's focus gates its events: unfocused, held buttons are released; focused again, each pad is read afresh. |
| `DesktopInputPump(…, gamepads: false)` | Keyboards and mice alone, when SDL has the pads, so no pad is seen twice. |
| `GamepadState.Type`, `.Family`, `.Vendor`, `.Product` | What a pad is, for prompts that name its buttons: a `GamepadType` (SDL's list: Xbox 360 and One, PS3–PS5, Switch Pro, Joy-Cons, GameCube, Steam, or `Unknown`), its `GamepadFamily` (whose labels its face buttons carry: Xbox, PlayStation, Nintendo, Steam), and its USB ids where the host has them. Carried by `InputEvent.GamepadConnected(pad, type, vendor, product)`: SDL gives the real type (behind Steam's virtual pad too), GLFW only its mapping's name, a browser its `Gamepad.id`; `GamepadIdentity` reads ids, names and ids' strings. |
| `InputSnapshot.LastDevice`, `.LastPad` | The device the player last used (keyboard, mouse, a pad, touch), for prompts that follow what is in hand: moved by a key or button going down, the wheel, a mouse moved more than 8 px, a stick past 0.4 or a trigger past 0.3, never by drift. |
| `Pad.Last` | The pad index meaning the pad last used (the first connected before any is). Every `Pad` helper and every implicit pad binding reads it, and so do `InputSnapshot.Down/Pressed/Released(GamepadButton)`, so whichever pad is picked up drives; `Pad.OnPad` pins a binding to one pad. |
| `InputBinding.Controls()`, `InputControl` | A binding broken into the physical controls it reads, in order (a composite's keys; a stick; the trigger pair), for drawing a glyph per control. `InputControl.Name(GamepadType)` names one as the pad in hand prints it ("Cross", "R2", "Create" on a DualSense; "A", "RT", "View" on an Xbox pad; "Left grip" on a Steam Controller); `ToBinding()` makes a binding of one. |
| `InputSnapshot.Touches`, `TouchPoint` | The touch contacts this frame, each by a stable `Id`: its `Position` now and at the frame's start (`Previous`, `Delta`), where it came down (`Start`), and its `Phase`: `Began` for the frame it came down in, then `Moved` or `Stationary`, then `Ended` or `Canceled` for one frame (`Gone`). A tap that comes and goes within one frame is reported once, ended, with `BeganThisFrame`. A touch is never a mouse: hosts send each finger as `InputEventKind.Touch`. |
| `Win32TouchPump` (desktop host) | A touch screen on Windows, which GLFW doesn't read: the window's procedure subclassed for `WM_POINTER` messages, each finger a contact in client pixels, and a touch handled there never promoted to a mouse; the touch feedback Windows draws is turned off for the window. A pen and the mouse go on to GLFW. The browser host reads the canvas's pointer events instead, with `touch-action: none` over it. |
| `InputActions.Press(action)` | Presses a button action from outside its bindings (an on-screen button, a tap on a prompt): the next `Resolve` finds it held, so it is pressed that frame and released the next, whatever its bindings are. |
| `InputActions.Names`, `KindOf`, `Bindings`, `Defaults`, `ResetToDefaults`, `ActionsUsing(control)` | For a rebinding screen: each action and what it reads now and as defined; putting one or all back; which actions already read a control. |
| `BindingCapture` | "Press a key": started from the snapshot as it is (whatever is held then is ignored until let go), polled each frame until a fresh key, mouse button, pad button, trigger past half way or stick direction past 0.6 from the families asked for (`Captured`, `Pad`), or the cancel key or button. |
| `GamepadButton.Misc1`, `RightPaddle1`, `LeftPaddle1`, `RightPaddle2`, `LeftPaddle2`, `Touchpad` | The buttons past the standard fifteen: a DualSense's mic button and touchpad click, a Steam Controller's grips (paddle 1 each side), an Elite's or DualSense Edge's paddles. GLFW and the browser report none of them. |

SDL 3 comes from the `BinaryVibrance.Kaldra.SDL3` package ([Kaldra.SDL3](https://github.com/AtomicBlom/Kaldra.SDL3)):
SDL's own release builds of `SDL3.dll` for win-x64 and win-arm64, with its licence, versioned as SDL is (3.4.20 is SDL
3.4.20). It carries no bindings. Where it has no build (anywhere but Windows), the host reads pads through GLFW. Our
own binding (`Sdl3`) is `[LibraryImport]` over blittable signatures only.

### `EngineSettings`

| Member | Purpose |
|---|---|
| `EnableValidation` | Enable the backend's validation/debug layer (on by default; degrades gracefully if absent). |
| `VSync` | Paced, tear-free present; when false the backend uses its lowest-latency present mode. |
| `MaxFramesPerSecond` | Frame-rate cap (0 = unlimited; only meaningful with `VSync` off). |
| `HdrOutput` | Request an HDR-capable swapchain + HDR output transform, falling back to SDR. |
| `PreferredBackend` | The backend `Name` to select (null = the first available). |
| `WatchShaderDirectory` | Hot-reload loose shaders rebuilt on disk (dev-only; off by default). |

`VulkanEngineSettings` adds `ShaderDirectory` and `PreferDiscreteGpu`. `DirectX12EngineSettings` adds `ShaderDirectory`, `PreferDiscreteGpu`, and `GpuBasedValidation` (on in Debug, off in Release).

### `IEngine` members

| Member | Description |
|---|---|
| `BackendName` / `DeviceName` | The backend name (e.g. `"Vulkan"`) and a human-readable GPU name. |
| `DefineTechnique(name, TechniqueDescriptor)` | Register a technique under a logical name passes reference. Idempotent per name. |
| `CreateMesh(ReadOnlySpan<Vertex>, ReadOnlySpan<uint> indices)` | Upload geometry in the canonical `MeshStandard` layout → `Mesh`. Between frames only: throws inside a pass. |
| `CreateMesh<T>(ReadOnlySpan<T>, VertexLayout, ReadOnlySpan<uint>)` | Upload geometry with an explicit layout (`sizeof(T) == layout.Stride`). |
| `CreateSkinnedMesh(ReadOnlySpan<SkinnedVertex>, ReadOnlySpan<uint>)` | Upload a skinned bind-pose mesh (readable by the compute pre-skin) → `Mesh`. |
| `ReleaseMesh(Mesh)` | Kill a mesh's handle at once; its buffers are freed once no frame in flight can read them. Between frames only. |
| `CreateInstances<T>(span, layout)` | Upload per-instance data once, to GPU-resident memory, for draws to take ranges of (`DrawCall.Static`): instances that don't change from frame to frame, which a CPU span would upload every frame. `sizeof(T)` must be the layout's stride. |
| `ReleaseInstances(InstanceBuffer)` | Free one, under `ReleaseMesh`'s rules. |
| `CreateTexture(TextureData)` | Upload an image + mip chain → `Texture`. Between frames only: an upload made inside a pass would miss the frame that samples it, so it throws there. |
| `CreateTextureAsync(TextureData)` | Queue an upload from any thread; completes when `ServicePendingUploads` (called by `Render`) drains it. |
| `ReleaseTexture(Texture)` | Kill a texture's handle at once; its image is destroyed once no frame in flight can read it. Throws for a texture a `Material` holds, and inside a pass. |
| `UpdateTexture(Texture, ReadOnlySpan<byte>)` | Write a whole new image into a texture in place, ahead of the next frame; frames in flight keep what they had. The handle, materials and bindings stay, and nothing allocates after a texture's first update. One mip level and one layer only, texels exactly its size; between frames only. |
| `CreateMaterial(params ReadOnlySpan<Texture>)` | Bundle textures (in the shader's binding order) → `Material`. A material holds its textures for the engine's lifetime. |
| `AllocateDrawListStream()` | Mint an engine-unique stream id for a stable `DrawListIdentity`, so draw-list producers never share kept geometry. |
| `SetGraph(RenderGraph)` | Make a graph active — derive its schedule/barriers/aliasing, reusing device + pipeline cache. Safe to call again to swap graphs. |
| `Write<T>(ConstantsHandle, in T)` | Write a value into a `g.Constants` buffer (reaches the reading pass on the next frame). |
| `Write<T>(StorageBufferHandle, ReadOnlySpan<T>)` | Write an array into a `g.Storage` buffer (the span counterpart). |
| `Render(FrameInputs)` | Render one frame of the active graph. |
| `Resize(int width, int height)` | Notify the engine the output surface resized (drives swapchain + relative-domain recreation). |
| `Read(ResourceHandle)` | Read back a `g.Readback` target's CPU copy as tightly-packed bytes (a frame or two old; empty before populated). |
| `RequestCapture(string path)` | Capture the next presented frame to a PNG (debug/validation). |
| `GpuTimingEnabled` | Toggle per-pass GPU timestamp collection (off by default; no graph rebuild). |
| `LastFrameTimings` / `LastFrameGpuMilliseconds` | Per-pass timings and total GPU time of the most recently completed frame. |
| `ResourceInspectionEnabled` / `InspectableResources` | Enable the resource viewer (set before `SetGraph`) and list the inspectable images. |
| `WaitIdle()` | Block until the GPU is idle (call before disposing host-owned resources). |

### `IRenderContext` members

| Member | Description |
|---|---|
| `Time` / `DeltaTime` | Elapsed seconds since startup / since the previous frame. |
| `HdrOutput` | `true` when the engine negotiated an HDR display output. |
| `Frame` | The frame being recorded: 0 for the engine's first `Render`, one more for each after, the same in every pass of a frame. |
| `FullScreen(technique, params ReadOnlySpan<ResourceHandle> inputs)` | Draw a fullscreen triangle, binding `inputs` as the technique's sampled inputs 0..N-1. |
| `FullScreen(technique, ReadOnlySpan<float> push, params ReadOnlySpan<ResourceHandle> inputs)` | As above, plus a small inline fragment-stage scalar block. |
| `Dispatch(technique, (uint X, uint Y, uint Z) groups, ReadOnlySpan<float> push, ReadOnlySpan<ResourceHandle> sampled, ReadOnlySpan<ResourceHandle> storage)` | Dispatch a compute technique over a workgroup grid. |
| `Draw(in DrawCall call)` | Submit one raster draw (the unified draw verb — see `DrawCall`). |
| `DispatchSkin(technique, Mesh skinnedMesh, ReadOnlySpan<Matrix4x4> palette, BufferHandle output, int vertexCount)` | Compute pre-skin a bind pose by a bone palette into `output` (a `.ReadWrites` buffer). |
| `DrawList<TVertex>(technique, DrawListIdentity identity, ReadOnlySpan<TextureRef> textures, ReadOnlySpan<TVertex> vertices, ReadOnlySpan<ushort> indices, ReadOnlySpan<DrawCommand> commands, ReadOnlySpan<float> push)` | Submit a batched indexed draw list (the overlay/UI path). Each command samples `textures[TextureIndex]` — an engine `Texture` or a graph texture (see `TextureRef`). `identity` is `DrawListIdentity.Transient` for a list rebuilt every frame, or a minted stream + revision for one whose geometry the backend may keep. |
| `ResolveInspectable(int resourceId)` | Resolve an inspectable resource id to a `Texture?` bindable in a draw list this frame — the resource viewer's hook, which needs no `.Samples` because inspection keeps every inspectable resource sampleable. |

### `FrameInputs`

A fresh value per frame (a `readonly record struct`, no allocation). The single channel for everything frame-varying — there is no engine `Camera`/`Sun`/`Scene`:

| Member | Description |
|---|---|
| `DeltaSeconds` (`double`) | Seconds since the previous frame; surfaced as `ctx.DeltaTime`. |
| `Time` (`double`) | Elapsed seconds since startup; surfaced as `ctx.Time`. |

View-projections, light, and time-of-day data are *not* here — publish them through `g.Constants`/`g.Storage` with `engine.Write(handle, value)` and read them in a pass with `.Reads`.

### `TechniqueDescriptor` (`Loom.Core.Materials`)

A record naming a shader *logically* and stating its pipeline intent; descriptor-binding counts are reflected from the shader, so you never hand-count them.

```csharp
engine.DefineTechnique("lit", new TechniqueDescriptor("lit", HasVertexInput: true));

// instanced, back-face culled, drawn from the Transform per-instance layout
engine.DefineTechnique("foliage", new TechniqueDescriptor("foliage",
    HasVertexInput: true,
    Instance: VertexLayout.Transform,
    Cull: CullMode.Back));
```

| Parameter (default) | Purpose |
|---|---|
| `Shader` | Logical shader name; the backend resolves bytecode + reflection. |
| `VertexEntry` (`"vertexMain"`) / `FragmentEntry` (`"fragmentMain"`) | Entry-point names. For a compute technique the single entry point sits in `FragmentEntry`. |
| `HasVertexInput` (`false`) | Reads a vertex stream (a mesh) vs fabricating a fullscreen triangle. |
| `Blend` (`BlendMode.None`) | Blend equation — pairs with a `.Blends` pass. |
| `LinearFilter` (`false`) | Sample inputs with linear vs nearest filtering. |
| `HasFragmentPush` (`false`) | The fragment/compute stage reads a small inline scalar block (up to sixteen floats). |
| `IsCompute` (`false`) | A compute technique — one entry point, dispatched not drawn. |
| `Address` (`SamplerAddress.Clamp`) | Sampler wrap mode for out-of-range coordinates. |
| `Layout` (`null`) | The per-vertex `VertexLayout` (defaults to `MeshStandard` when `HasVertexInput`). |
| `Instance` (`null`) | The per-instance `VertexLayout` (e.g. `Transform`) for instanced draws. |
| `HasListPush` (`false`) | A draw-list technique's all-stage push block (whose last slot each command's `Tag` rewrites). |
| `Cull` (`CullMode.None`) | Which faces the rasterizer discards — front faces are counter-clockwise. |
| `DepthTest` (`DepthCompare.LessOrEqual`) | The depth comparison a fragment must pass, when the pass has a depth target. |
| `Bias` (`null`) | A constant + slope-scaled depth offset (`DepthBias`) — the shadow-acne knob. |
| `AlphaToCoverage` (`false`) | In a multisampled pass, the fragment's alpha decides how many of its samples it covers, so a cut-out (a leaf, a fence's wire) is edged as smoothly as geometry rather than stepped by a discard. How alpha becomes a sample mask is the driver's choice and differs between APIs on one GPU, so such edges aren't at pixel parity across backends. No effect in a pass of one sample. |
| `SampleCompare` (`null`) | The compare function of the shader's comparison samplers: a `Sampler2DShadow` read with `SampleCmp` passes where `reference [compare] stored` (a reversed-depth shadow map takes `GreaterOrEqual`). Which inputs compare is found in the shader (its SPIR-V's depth-comparison samples; WebGPU's `sampler_comparison`); they get a filtered (2×2 PCF), edge-clamped comparison sampler and the rest keep `LinearFilter`'s. A shader that compares without it, or a technique naming it whose shader doesn't, throws at `DefineTechnique`, as does a comparison the SPIR-V can't trace to its texture (one through a local, a phi or a function parameter). The compared texture is a depth target the pass `.Samples`: a material's textures are colour formats, which no backend compares, so a material for a comparing technique throws at `CreateMaterial` or at the draw, and so does a draw-list technique naming `SampleCompare`. |

Shaders are authored in **Slang** and compiled offline to SPIR-V (Vulkan), signed DXIL (DirectX 12), and WGSL (WebGPU) with reflection JSON. The Slang-side half of the contract — the platform macros, the two independent numbering schemes, and where a push block actually lands — is [`shader-authoring.md`](shader-authoring.md).

### Pipeline-intent enums

| Enum (`namespace`) | Members |
|---|---|
| `BlendMode` (`Loom.Core.Graph`) | `None`, `AlphaOver`, `Additive`, `PremultipliedAlpha`, `Multiply`. |
| `CullMode` (`Loom.Core.Graph`) | `None`, `Back`, `Front` (front faces are counter-clockwise). |
| `DepthCompare` (`Loom.Core.Graph`) | `Never`, `Less`, `Equal`, `LessOrEqual`, `Greater`, `NotEqual`, `GreaterOrEqual`, `Always`. |
| `SamplerAddress` (`Loom.Core.Materials`) | `Clamp`, `Repeat`, `MirrorRepeat`, `ClampToBorder`, `MirrorClampToEdge`. |

### Reversed-Z

Reversed-Z puts the far plane at depth 0, which cancels the perspective divide's precision loss at distance against a float depth buffer's density near zero — the standard fix for depth fighting in a large world. It is **three declarations that must agree**; getting one wrong doesn't fail, it inverts or erases occlusion:

```csharp
var depth = g.Screen.Depth("depth", Format.Depth32F, clearDepth: 0f);          // 1. far plane at 0
camera.Perspective(fovY, aspect, 0.1f, 5000f, reversed: true);                 // 2. reversed projection
engine.DefineTechnique("mesh", new TechniqueDescriptor("mesh",
    HasVertexInput: true, DepthTest: DepthCompare.GreaterOrEqual));            // 3. reversed test
```

Viewport `MinDepth`/`MaxDepth` stay `0`/`1` — the reversal lives in the projection, not the viewport.

**Depth bias is only partly portable.** `DepthBias.SlopeScale` means the same thing on every backend. `DepthBias.Constant` does not: Vulkan multiplies it by an implementation-defined `r`, D3D12 applies `Constant × 2^(exponent−23)` for a float depth buffer. Prefer the slope-scaled term, or tune the constant per backend.

## Drawing, instancing, skinning (`Loom.Core.Hosting`)

`ctx.Draw(in DrawCall)` is the unified raster draw verb. `DrawCall` is a `ref struct`; build the common case with `DrawCall.Create(mesh, technique)`, or use an object initializer for richer calls.

| Member | Role |
|---|---|
| `DrawCall.Create(mesh, technique)` | The terse single-mesh, no-material case. |
| `Technique` (required `string`) | The technique that selects the pipeline. |
| `Mesh` (`Mesh?`) | The mesh to draw; `null` for procedural draws (set `VertexCount`). |
| `SkinnedVertices` (`BufferHandle?`) | A pre-skinned vertex buffer override (the mesh still supplies its indices); must be a `.ReadsVertex` buffer. |
| `VertexCount` (`int`) | Vertices per instance for procedural draws (when `Mesh` is null). |
| `Instances` (`InstanceSource`) | Per-instance data — empty (default) = single draw. |
| `Indices` (`IndexRange`) | Which sub-range of the mesh to draw; empty (default) = the whole mesh. |
| `Material` (`Material?`) | Material whose textures fill the technique's sampler slots; `null` for untextured techniques. |
| `Uniforms` / `.SetUniforms<T>(ref T)` / `.SetUniforms<T>(ReadOnlySpan<T>)` | The per-draw uniform block (raw bytes matching the shader's cbuffer); set it from a typed value or span. |
| `Sampled` (`ReadOnlySpan<ResourceHandle>`) | Graph resources bound as sampled inputs (each declared via `.Samples`). |
| `DrawCall.From<T>(ReadOnlySpan<T>)` | Build an `InstanceSource` from a CPU instance span (uploaded per frame). |
| `DrawCall.Gpu(BufferHandle, count)` | Build an `InstanceSource` from a GPU-resident buffer a compute pass filled. |
| `DrawCall.Static(InstanceBuffer, first, count)` | Build an `InstanceSource` from a range of instances uploaded once (`IEngine.CreateInstances`); nothing is uploaded per frame. The range is checked against the buffer, and the technique's instance stride against its stride. |
| `DrawCall.Indirect(BufferHandle, byteOffset)` | Build an `InstanceSource` whose counts come from a buffer (declared `.ReadsIndirect`); `Indices` is ignored — the range comes from the buffer. |
| `DrawCall.Range(firstIndex, indexCount, baseVertex)` | Build an `IndexRange`, so one mesh can hold several primitives. |

`InstanceSource` (a nested `ref struct`) exposes `IsEmpty` and `Count`; construct it only via `DrawCall.From` / `DrawCall.Gpu` / `DrawCall.Static` / `DrawCall.Indirect`. An indirect draw has no CPU-known `Count` — it reports zero, because only the GPU knows. Skinning is `ctx.DispatchSkin(...)` (a compute pre-skin into a `.ReadsVertex` buffer) followed by an ordinary `ctx.Draw` of that buffer.

## Geometry & materials

| Type (`namespace`) | Role |
|---|---|
| `Mesh` (`Loom.Core.Geometry`) | An opaque handle to uploaded geometry from `IEngine.CreateMesh` / `CreateSkinnedMesh`. Survives graph swaps; dies at `IEngine.ReleaseMesh`. |
| `InstanceBuffer` (`Loom.Core.Geometry`) | A handle to instances uploaded once by `IEngine.CreateInstances`: its count and stride, stored as a mesh is. Dies at `IEngine.ReleaseInstances`. |
| `Vertex` (`Loom.Core.Geometry`) | The canonical interleaved vertex — `record struct (Vector3 Position, Vector3 Normal, Vector2 Uv, Vector4 Tangent)`, 48 bytes. |
| `SkinnedVertex` (`Loom.Core.Animation`) | The skinned bind-pose vertex — `Vertex` fields plus four bone indices (`Joint0..3`) + `Vector4 Weights`, 80 bytes. |
| `OverlayVertex` (`Loom.Core.Geometry`) | The 2D overlay vertex (position, uv, premultiplied RGBA16F colour, custom) used by draw lists. |
| `viewProjection.TryProjectToScreen(world, viewportSize, clipSpaceCorrection, out screen, out depth)` (`Loom.Core.Geometry`) | Extension member on `Matrix4x4`: a world point → viewport pixels (top-left origin, +Y down) and NDC depth, through the view-projection an app hands its shaders. `clipSpaceCorrection` is `IEngine.ClipSpaceCorrection`, which says which way the backend's NDC Y runs, so the pixel is the same on every backend. False when the point is on or behind the eye's plane. |
| `viewProjection.TryUnprojectFromScreen(screen, depth, viewportSize, clipSpaceCorrection, out world)` (`Loom.Core.Geometry`) | The inverse: the world point under a viewport pixel at an NDC depth (0 and 1 give the near and far ends of its ray). |
| `viewProjection.GetFrustumCorners()` / `ToFrustum()` (`Loom.Core.Geometry`) | Extension members on `Matrix4x4`: the eight world-space frustum corners, and the six planes as a `Frustum`. |
| `VertexLayout` (`Loom.Core.Geometry`) | A backend-neutral per-stream attribute layout. Built-ins: `MeshStandard`, `MeshSkinned`, `Overlay`, `Transform` (the standard per-instance 4×4 matrix stream). |
| `Material` (`Loom.Core.Materials`) | An opaque `record struct (int Id)` — an ordered `Texture` bundle from `IEngine.CreateMaterial` that fills a technique's reflected sampler slots. |
| `Texture` (`Loom.Core.Materials`) | An opaque handle to an uploaded texture from `IEngine.CreateTexture`. Survives graph swaps; dies at `IEngine.ReleaseTexture`. Ids start at 1, so `default(Texture)` names nothing and is refused. |
| `TextureData` (`Loom.Core.Materials`) | A neutral image description (pixel data + mip chain). Built via `FromRgba8`, `FromRgba16F`, `FromCubeFaces`; carries `Format` (`TextureFormat`), `Width`, `Height`, `Levels`, `IsCube`. |
| `TextureFormat` (`Loom.Core.Materials`) | Source-image encoding (distinct from `Format`): `Rgba8Srgb`, `Rgba8Unorm`, `Rgba16F`. |

## Assets (`Loom.Assets`)

Neutral loaders beside `Loom.Core` (no graphics-API reference). They emit `TextureData` / `Vertex` arrays / a `Skeleton` / `AnimationClip`s; the app turns those into engine meshes, textures, and materials.

| Member | Role |
|---|---|
| `ImageLoader.Load(path, TextureFormat format, generateMips=true)` | Decode a PNG/JPG/… file to `TextureData` with a CPU mip chain. |
| `ImageLoader.Decode(Stream, TextureFormat, generateMips=true)` | Decode an encoded image from a stream (e.g. a glTF-embedded image). |
| `ImageLoader.LoadHdr(path)` / `ImageLoader.DecodeHdr(Stream)` | Decode a Radiance HDR / float image to `Rgba16F` `TextureData`. |
| `GltfLoader.Load(path)` | Load a glTF/GLB model → `LoadedModel`. |
| `LoadedModel` | `Primitives` (`IReadOnlyList<ModelPrimitive>`), `Skeleton?`, `Clips` (`IReadOnlyList<AnimationClip>`). |
| `ModelPrimitive` | `Vertices` (`Vertex[]`), `Indices` (`uint[]`), `Material` (`ModelMaterial?`), `SkinnedVertices` (`SkinnedVertex[]?`), `IsSkinned`. |

## Overlay draw lists (`Loom.Core.Hosting`)

The cross-backend verb behind overlay/UI-style rendering (game UI via SUIsei, ImGui, debug lines): a pass stages a per-frame, variable-size indexed vertex stream and submits it with `ctx.DrawList<TVertex>(...)`. There is no `FrameInputs`/`ctx` channel for this — a pass gets its geometry from whatever stable producer it holds as pass state (a painter), reading its live contents each frame.

| Type | Role |
|---|---|
| `DrawCommand` | One run within a list — `record struct (Rect ClipRect, int TextureIndex, int IndexOffset, int IndexCount, int VertexOffset, uint Tag)`. `Tag` is the per-command value a list-push technique reads. |
| `DrawListIdentity` | `record struct (int Stream, uint Revision)` — which kept geometry stream a list is and which revision of it. `Transient` (revision 0) keeps nothing; a stable stream comes from `IEngine.AllocateDrawListStream()` and is refused otherwise, because two producers sharing one would draw each other's geometry. |
| `TextureRef` | What one slot of a list samples: an engine `Texture` (implicit from `Texture`) or a graph texture an earlier pass rendered this frame (implicit from `ColorHandle`/`DepthHandle`, or `new TextureRef(ITextureHandle)`). A value type, so a producer keeps an array of them and rewrites it in place. A graph texture is an ordinary sampled input: the submitting pass declares it with `.Samples` or the list throws `PassBindingException`, and its format must sample as a filterable float (`Format.IsFilterableFloat`), so a depth, integer or `R32F` target is refused on every backend. The backend resolves it to the live image each submission, so a resize needs nothing from the caller. |

A list that shows a target the frame rendered — a minimap, a portal, a render-to-texture preview — declares it like any other sampled input and names its handle in the texture span:

```csharp
var map = g.Domain("map", 256, 256).Color("map.color", Format.Rgba8Srgb);
g.Pass("map").Reads(mapViewProj).Writes(map).Draw(ctx => DrawTopDown(ctx));
g.Pass("minimap").Samples(map).Blends(g.Backbuffer)        // .Samples orders it after "map" and makes it readable
    .Draw(panel, static (ctx, panel) => ctx.DrawList("ui", DrawListIdentity.Transient,
        panel.Textures,                                    // TextureRef[]: [whiteTexel, map]
        panel.Vertices, panel.Indices, panel.Commands, panel.Push));
```

### UI bridges

The UI libraries reach the GPU only through `DrawList`; each bridge is a stable producer a pass holds as state.

| Type (`namespace`) | Role |
|---|---|
| `UIPainter` (`Loom.UI.SUIsei`) | `UIPainter.Create(engine, host)` registers the `ui` technique and returns the painter for a SUIsei `UIHost`. Call `Prepare()` once per frame after the host composites and before `engine.Render`: it uploads the images the frame's slot tables newly name, writes the new pixels of a source whose pixels changed into its texture in place (`IEngine.UpdateTexture`) and releases one nothing references any more (after the frames in flight, through `IEngine.ReleaseTexture`). `Draw(ctx, surface)` inside a pass only binds and submits, as many times in the frame as it likes, and throws if `Prepare` did not run before the frame. `SetExternalTexture(tag, ITextureHandle)` binds a graph texture to every SUIsei `HostImageSource` with that tag; the UI pass declares it with `.Samples`, and a tag with no texture, or a colour space its format does not decode, fails `Prepare`. A source uploads as `Rgba8Srgb` or `Rgba8Unorm` by its colour space, and a straight-alpha one is premultiplied after filtering, as SUIsei's rasterizer does. Textures the host reports released (`UIHost.ReleasedTextures`) are released in `Prepare`; a `WriteableBitmap`'s texture is updated in place on each `Invalidate`. |
| `SUIseiInputLayer` (`Loom.UI.SUIsei`) | The input half: an `IInputLayer` that queues a frame's pointer, key and text events into a `UIHost` and blocks the layers below from what the host reported it wants. `PadNavigation`, while the app sets it (a menu or page a pad can work is open), turns any pad's face buttons into SUIsei's `GamepadA`/`B`/`X`/`Y` and its d-pad and left stick into the d-pad's keys, a held direction repeating after 0.4 s every 0.09 s, and blocks the pad from the layers below: SUIsei moves focus by position on the d-pad, and `GamepadA` presses a button. A touch that comes down on UI content (`UIHost.HitsContent`) is the UI's until it lifts, sent as a touch pointer and kept from the layers below; any other is theirs for its life, so a finger on a button and another on the game work at once. The block on what the host wants covers the mouse (and a pen's moves), never a touch. |
| `InWorldPanel` (`Loom.UI.SUIsei`) | A SUIsei surface rendered into its own texture and placed in the world: `Create(engine, width, height, worldTransform, billboard, layoutScale)`, then per frame `QueueScreenPointer`, `Tick`, `Render` (composite + prepare) and `Draw(ctx)` in the pass that renders the panel's target. |
| `ImGuiPainter` (`Loom.UI.ImGui`) | `ImGuiPainter.Create(engine)` registers the `imgui` technique and uploads the font atlas; `Draw(ctx)` submits the frame's Dear ImGui draw data, plus the resource viewer's thumbnails (`SetThumbnails`, through `ResolveInspectable`) and debug lines (`SetDebugLines`). Desktop only. |

## World-anchored UI (`Loom.UI.SUIsei`)

Screen-space SUIsei elements that follow points in the world — labels over objects, markers along a path. App-side: the engine names no anchor; this sits in the SUIsei bridge on top of `TryProjectToScreen`.

| Member | Role |
|---|---|
| `ScreenAnchors.Create(engine, view)` | Anchors that fill `view`, a `UIView` of their own (its root must be empty), reading `engine.ClipSpaceCorrection` once. Draw `anchors.View.Surface` with a `UIPainter` like any view. |
| `anchors.Add(element, world, alignment, pixelOffset)` → `ScreenAnchorId` | Wrap `element` in a container pinned to `world`: `alignment` is the fraction of the element's size that sits on the point ((0.5, 1) the middle of its bottom edge), `pixelOffset` a further offset in layout units. |
| `anchors.SetWorldPosition(id, world)` / `SetPlacement(id, alignment, pixelOffset)` / `Remove(id)` / `GetFade(id)` | Move, re-place, remove (a removed id throws if used again) and read an anchor's fade. |
| `anchors.Update(viewProjection, framebufferSize, deltaSeconds)` | Each frame between the host's `Tick` and `Render`: move every container to its projected point through `UIElement.Translation` (no layout) and step its fade — out while the point is off the viewport (beyond `ViewportMargin`) or behind the eye, on the container, so the element's own opacity is untouched. Allocates nothing. `FadeSeconds` sets the fade's length. |

## Audio (`Loom.Audio`)

Clips and game-written sources played on pooled voices, mixed through a tree of buses on the platform's audio thread. Apart from the engine: an app registers its platform's output (`Loom.Audio.Sdl`, `Loom.Audio.Android`, `Loom.Audio.Web`) and makes its own `AudioEngine` over it. The guide, with the threading model and each platform's device, is [`audio.md`](audio.md).

| Type / member | Role |
|---|---|
| `SdlAudio.Register()`, `AndroidAudio.Register()`, `await WebAudio.RegisterAsync()` | Each platform's output, registered once by the app's entry point for that platform, before anything opens audio. |
| `AudioOutput.Open(AudioOptions)` → `IAudioDevice`, `AudioOutput.Register(name, open)` | The first registered output that opens, or a `NullAudioDevice` saying why none did. |
| `AudioOptions` | `SampleRate` and `BufferFrames` default to 0: the device's own rate and its smallest buffer. `MaxVoices` (64), `MaxBuses` (16), `CommandCapacity` (4096). |
| `new AudioEngine(device, options)` | The app's engine, which starts the device. Called from the frame thread only (another thread throws), except `CreateClip`/`LoadWav`. The app calls `Update()` once a frame (which pumps the device: the browser's mix is sent then), `Pause()`/`Resume()` as it leaves the screen and comes back, `SetFocused` as focus changes, and `Dispose()` as it closes. |
| `CreateClip(samples, channels, sampleRate)`, `LoadWav(bytes)` → `AudioClip` | Sound decoded into memory and converted to the engine's rate. WAV: PCM 8/16/24/32-bit, float 32/64, mono or stereo. Any thread. |
| `Play(clip, bus, volume, pitch, pan, loop)` / `Play(source, bus, volume, pan)` → `VoiceHandle` | Takes a voice from the pool; `VoiceHandle.None` when every voice is busy (counted in `Stats.DroppedVoices`). |
| `SetVolume`, `SetPitch`, `SetPan`, `Stop(voice, fadeSeconds)`, `IsPlaying(voice)` | Steer a voice. Every change ramps across at least a block, so nothing clicks. A stale handle (its voice ended) is ignored. |
| `CreateBus(name, parent)` → `AudioBus`, `SetBusVolume(bus, volume, fadeSeconds)`, `Master` | A tree of buses under the master; mix order follows from the tree. Made at load, never removed. |
| `AudioEffect`, `AddEffect(bus, effect)` | Subclass for something that changes a bus's sound: `OnPrepare(sampleRate)` on the frame thread, `Process(left, right)` in place on the audio thread. Runs before the bus's volume, in the order added; `Bypass` turns it off. One bus per instance. At most `MaxEffectsPerBus` (8) a bus. |
| `AddSend(from, to, level)` → `AudioSend`, `SetSendLevel`, `GetSendLevel` | A share of one bus mixed into another (a reverb bus), besides its parent; the engine derives the order to mix them in, and refuses a loop. At most `MaxSends` (16). |
| `AudioSource` | Subclass for sound the game writes: `OnStart(sampleRate)` on the frame thread, `Render(left, right, in AudioRenderInfo)` on the audio thread (no allocation, locks or blocking; false ends the voice; an exception stops the voice and is thrown again from `Update`). One voice at a time: `IsPlaying` holds until the voice has ended, a stop's fade included. |
| `AudioRenderInfo` | The block's `Frames`, the engine's `SampleRate`, and `FrameTime`, the audio clock a sequencer counts on. |
| `AudioParameter` | A continuous control: the game sets `Value`; the source takes `Ramp(frames)` once a block and `Next()` once a sample, or `Snap()`. |
| `SmoothedParameter` | A continuous control eased with a time constant, for a value set in steps many times a second: `Prepare(rate)` in `OnStart`, `Begin()` once a block, `Next()` once a sample or `Skip(frames)`, `Snap()`. |
| `AudioEventQueue<T>` | A fixed-size, lock-free, one-producer one-consumer queue of struct events (note on, note off): `TryPost`/`Post` from the game, `TryTake` in `Render`. |
| `AudioStats` (`Audio.Stats`) | The mix's clock, callbacks and buffer, active and dropped voices, peak level, and render load against the deadline. |
| `IAudioDevice`, `IAudioRenderer` | The seam each output fills: SDL 3 (desktop), AAudio (Android), an AudioWorklet (browser), whose `Pump` renders and sends the mix once a frame. `NullAudioDevice` where there is no output; `OfflineAudioDevice` renders on the caller's thread, for tests and rendering to a file. |
