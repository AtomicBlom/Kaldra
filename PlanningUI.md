# Loom XAML UI Engine — Implementation Plan

> Companion to `Planning.md` (the design bible) and `PlanningPhase2.md` (the M11–M16 asset/scene
> roadmap). This document covers the **UI track** — a WinUI-3-compatible XAML UI engine. It is the
> source of truth for *what* the UI track is; the GitHub Projects board (#6) tracks live status.

## Context

Loom today is a render-graph + *effects* engine with no UI layer beyond a Dear ImGui debug HUD.
This adds a **lightweight, WinUI-3-compatible XAML UI engine** for **game UI / HUDs**, rendered on
Loom's own GPU backends (Vulkan + DX12) at pixel parity. "Lightweight" means *small footprint +
NativeAOT-clean*, not few features — the v1 scope is broad on purpose.

The defining constraint, decided with the user, is that the whole thing is **compile-time, not
reflection-time**: XAML and `{x:Bind}` compile to strongly-typed C# at build, so games can ship with
**NativeAOT**. This fits Loom's existing DNA — the offline Slang→SPIR-V/DXIL toolchain
([Loom.Shaders.targets](build/Loom.Sdk/Loom.Shaders.targets)) and the "ship finished blobs, don't ship
a compiler" philosophy are the same idea applied to shaders. The UI compiler and the MSDF font baker
become siblings of that toolchain.

The intended outcome: author WinUI-syntax `.xaml` (with `{x:Bind}`, styles, templates, storyboards),
have it compile to a typed control tree, lay it out, render it crisply (screen-space **and** on an
interactive 3D quad), and drive it with mouse + keyboard + focus + gamepad + touch — all reflection-free.

## Locked decisions

| Axis | Decision |
|---|---|
| Dialect | **WinUI 3** (`Microsoft.UI.Xaml`) syntax — a curated, published *subset* (see Risks). |
| Binding | **`{x:Bind}` only**, compiled. No reflection `{Binding}`. |
| Runtime | **NativeAOT-clean** end to end — no reflection in property system, binding, layout, or hot paths. |
| Precompiler | **One Roslyn incremental source generator** consuming `.xaml` as `AdditionalFiles`, emitting both the control-tree construction *and* the typed `x:Bind`/event/storyboard glue. No MSBuild task. |
| Text | **MSDF prebaked glyph atlases**, baked offline (sibling to the Slang toolchain). HarfBuzzSharp shaping at runtime. |
| Surfaces | **Screen-space overlay AND fully interactive in-world UI** (render-to-texture on a 3D quad; CPU ray→UV→hit-test). |
| Input | Pointer (mouse+touch) + keyboard + text entry + focus/Tab + **gamepad/XYFocus spatial nav**, with WinUI routed-event (tunnel/bubble) semantics. |
| Animation | Storyboards/keyframes/easing + `VisualStateManager`, in v1. |
| ImGui | **Coexists** — ImGui stays as the dev/debug HUD; this is the game-facing UI. |

## Architecture

**The central pattern: clone the ImGui renderer path.** All three design passes independently landed on
this. ImGui already proves, on both backends, exactly the seam this UI needs: a backend-private renderer
fed neutral per-frame draw data through a neutral `IRenderContext` verb, recorded into the open pass scope.
We mirror it for UI. This preserves the cardinal invariant — **no graphics-API type enters Core/Graph** —
because the UI emits backend-neutral draw data and the backends turn it into GPU work.

Reference seam to mirror:
- Neutral verb [`IRenderContext.DrawImGui()`](Loom.Core/IRenderContext.cs) → add `DrawUI(...)`.
- Backend renderer [`Loom.Vulkan/ImGuiRenderer.cs`](Loom.Vulkan/ImGuiRenderer.cs) and
  [`Loom.DX12/DX12ImGuiRenderer.cs`](Loom.DX12/DX12ImGuiRenderer.cs) → add `UiRenderer` / `DX12UiRenderer`.
- Per-frame hand-off via [`FrameInputs`](Loom.Core/FrameInputs.cs) (like `ImGuiDrawData`) → add `UiSurfaces`.
- Wire-in via `EnableImGui()` in [`VulkanEngine.cs`](Loom.Vulkan/VulkanEngine.cs) → add `EnableUI()`.

### New projects (dependency direction preserved: `Sandbox → {Vulkan, DX12} → Graph → Core`)

| Project | TFM | Role | References |
|---|---|---|---|
| **`Loom.UI`** | `net10.0` | Backend-neutral UI runtime: dependency-object model, controls, layout/visual tree, routed events, focus, hit-testing (2D + 3D ray), `UiHost`, draw-list builder, animation, text layout. | **`Loom.Core` + `Loom.Assets`** (both neutral) |
| **`Loom.UI.SourceGen`** | `netstandard2.0` | Roslyn `IIncrementalGenerator`: `.xaml` → typed control-tree builders + `x:Bind`/event/storyboard/visual-state glue. Ships as `<Analyzer>`. Build-time only. | `Microsoft.CodeAnalysis.CSharp` (host-pinned, `PrivateAssets=all`) |
| **`Loom.UI.Text.Bake`** | `net10.0` | Build-time console tool (never shipped): font subset → MSDF atlas PNG + metrics JSON. Orchestrates `msdf-atlas-gen`. | — |
| `build/Loom.Sdk/Loom.UI.{props,targets}` | — | Register `.xaml` as `AdditionalFiles` for the generator (+ `UpToDateCheck`); run the font bake + register atlas/JSON as Embedded/Content. Mirrors `Loom.Shaders.{props,targets}`. | — |

`Loom.UI` depends on `Loom.Core` + `Loom.Assets` (both neutral, Core-only, no GPU types) and **must not**
reference Graph/Vulkan/DX12 — it reaches the GPU solely through the neutral seams (`RenderGraph.Define`,
`IEngine`, `IRenderContext`), exactly as an app does. The `Loom.Assets` reference is what lets the runtime
`FontAtlas` decode baked MSDF pages via `ImageLoader.Decode` (shipped M11) rather than re-wrapping StbImageSharp.
Naming follows existing repo casing (`Loom.Core`, `Loom.DX12`): the project is `Loom.UI`.

### Core surface additions (the only changes to Core — all neutral, no GPU types)

These are managed neutral data types, the same way [`Vertex`](Loom.Core/Vertex.cs),
[`Mesh`](Loom.Core/Mesh.cs), and [`Camera`](Loom.Core/Camera.cs) already live in Core:

- `UiVertex` (pos, uv, premultiplied RGBA8, params), `UiPrimKind` (Fill/RoundedRect/Border/Glyph/Image),
  `UiDrawCommand` (kind, textureId, clipRect, index range), and `UiDrawData` (vertex/index/command spans +
  surface size + `IsHdrSurface` flag + `Revision`).
- `IEngine.EnableUI()` — stand up the backend `UiRenderer` + upload baked atlas pages once.
- `IRenderContext.DrawUI(UiSurfaceId)` — record a named surface's draw data into the open pass scope; no-op
  until enabled. (Multi-surface: overlay + N in-world quads each have an id.)
- `FrameInputs.UiSurfaces` — the per-frame set of `(UiSurfaceId, UiDrawData)`; pulled in the closure, never
  captured (preserves the pure-closure invariant).

### Shaders (under [Loom.Shaders/Shaders](Loom.Shaders/Shaders), embedded like `imgui.slang`)

One `ui.slang` (vertex + a fragment that switches on a flat `uint kind`): analytic rounded-rect/border SDF
(AA via `fwidth`), MSDF glyph (`median(rgb)`, `screenPxRange` smoothstep), and tinted image sampling. Two
pipelines per color space (alpha-over + optional additive for glow). Compiled to SPIR-V (Vulkan) + signed
DXIL (DX12) by the existing dual-emit toolchain. Like ImGui, it's driven by a bespoke `UiRenderer` (custom
blend + dynamic scissor) rather than the generic technique path.

## The source generator (`Loom.UI.SourceGen`)

`IIncrementalGenerator`; `.xaml` arrives as `AdditionalFiles` (registered by `Loom.UI.props`). For each
page/template it emits a `partial` of `x:Class` with: typed `x:Name` fields; `InitializeComponent()` that
builds the tree with generated `new` (**never `Activator.CreateInstance`**); literal/attached/`StaticResource`
value assignment; typed event wiring (`btn.AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler(this.OnClick))`,
signature-checked against the control's event); and a nested `Bindings` class for `{x:Bind}`.

`x:Bind` specifics (resolved via the `Compilation` semantic model — `GetTypeByMetadataName(x:DataType)`):
direct typed member-access paths (no `PropertyInfo`); `OneTime`/`OneWay`/`TwoWay` modes; function paths;
INPC change tracking by a generated handler that `switch`es on `e.PropertyName` **string literals** and calls
the specific typed `Update_N()`; deep-path re-subscription; converters as generated singletons;
`fallback`/`target-null` as inline literals. Diagnostics carry the XAML `file:line` so binding errors squiggle
in the `.xaml`. Styles/templates/resources compile to typed setter delegates and `Func<>` factories — every
"create" is a `new`, every "set" a typed setter (the NAOT payoff).

Commit to a **curated WinUI-syntax subset** published as a hard contract (see Risks); the generator errors
clearly on anything outside the grammar.

## Runtime object model + layout (`Loom.UI`)

- **DependencyObject / `DependencyProperty<TOwner,TValue>`**, reflection-free: dense `GlobalIndex` slots
  assigned at registration via `[ModuleInitializer]` (the [`VulkanBackend`](Loom.Vulkan/VulkanBackend.cs)
  self-registration pattern); typed `GetValue<T>`/`SetValue<T>` boxing-free on the value-type fast path;
  typed `PropertyChanged`/`Coerce` delegates (no `MethodInfo`); attached props share the index space, stored
  on the target. Follows the abstract-base + sealed-subtype + marker-interface convention of
  [`ResourceHandle.cs`](Loom.Core/ResourceHandle.cs).
- **Value-precedence ladder (with the animation layer present from day one):**
  `Animation > Local > Style > Inherited > Default`. A set recomputes the top of the ladder, fires the typed
  change thunk, and ORs `InvalidationFlags` (Measure/Arrange/Render) up the tree. The animation layer is a
  hard requirement for storyboards (MU4) and must exist before then.
- **Element hierarchy:** `UIElement` → `FrameworkElement` → `Panel`(`Grid`/`StackPanel`/`Canvas`) /
  `Border` / `ContentPresenter` / `ItemsPresenter` / `TextBlock` / `Image` / `Control`(`ContentControl→Button`,
  `ItemsControl→ListView`).
- **Two-pass measure/arrange** (WinUI semantics): `Measure` deflates Margin, clamps Min/Max/explicit,
  `MeasureOverride`, reinflates → `DesiredSize`; `Arrange` applies alignment + `ArrangeOverride`. `Grid` does
  Pixel/Auto/Star track resolution; `StackPanel`/`Canvas` straightforward. Invalidation via dirty flags +
  per-`UiRoot` measure/arrange queues, with a **frame-level no-op** when nothing is dirty (re-submit last
  `UiDrawData.Revision`).
- **DPI:** layout in DIPs; the render walk scales by `LayoutScale` (window DPI for overlay; author-chosen texel
  density for in-world). MSDF makes this resolution-independent — no per-DPI re-bake.

## Rendering + text

- **2D model:** retained tree, draw-list rebuilt only on dirty frames. The walk resolves transform/clip/opacity
  stacks into a pre-transformed `UiVertex` stream + `UiDrawCommand`s. **Atlas-everything + state-run batching**
  (white texel for fills; MSDF pages for glyphs; image atlas/in-world targets for images) → thousands of
  rects/glyphs in one indexed draw; a new command only on clip/texture/kind change. GPU upload path is a clone
  of [`ImGuiRenderer`](Loom.Vulkan/ImGuiRenderer.cs) (per-frame-in-flight grown buffers, scissored
  `CmdDrawIndexed`).
- **MSDF text — build time:** `Loom.UI.Text.Bake` reads a `*.fontset.json` (fonts + charset ranges/scripts +
  `pxRange`/`glyphSize`), drives `msdf-atlas-gen` (MIT, build-only, bundled hermetically like `slangc`), emits
  atlas PNG page(s) + a metrics JSON (per-glyph UV/plane/advance, font metrics, subset cmap) — the text analog
  of the Slang reflection sidecar. An MSBuild target (`LoomCompile/RegisterFonts`) mirrors
  [`Loom.Shaders.targets`](build/Loom.Sdk/Loom.Shaders.targets) for embed/Content packaging.
- **MSDF text — runtime:** `TextLayoutEngine` itemizes (script/dir/font), shapes with **HarfBuzzSharp** (MIT;
  native P/Invoke = no reflection, NAOT-OK), line-breaks/wraps, and emits glyph runs; shaping cached by
  `(text, font, size, width)`. Charset subset is driven by the localization string table; coverage gaps fall
  back through a `FontFallbackChain`, then a `.notdef`/tofu glyph; **the bake tool warns at build time** on any
  string-table codepoint with no covering font. (NAOT-purist escape hatch: bake-time shaping of fixed strings,
  documented, not v1.) PNG decode reuses **`ImageLoader.Decode`** ([`Loom.Assets`](Loom.Assets/ImageLoader.cs),
shipped in M11; StbImageSharp-backed) → neutral `TextureData`; the UI makes no direct StbImageSharp call.
- **Compositing:**
  - *Overlay* — after upscale, at native res, blends the backbuffer:
    `g.Pass("ui").Blends(screen).Draw(ctx => ctx.DrawUI(UiSurface.Hud))` (the
    [HUD pass](Loom.Sandbox/Demos/IDemo.cs) shape; `.Blends` = load + alpha-over).
  - *In-world* — render into a `domain.Color` target, then `.Samples` it on a quad — the shadow-map
    ([ShadowsDemo.cs](Loom.Sandbox/Demos/ShadowsDemo.cs)) / bloom-pyramid
    ([BloomGraph.cs](Loom.Sandbox/Demos/BloomGraph.cs)) pattern. Transient by default, `Persistent` for
    slow-updating surfaces. **No new describe-layer resource concept** — only the `DrawUI` verb + the surface channel.

## Input, interaction, animation (`Loom.UI`)

- **Pipeline:** the host (Sandbox, Silk.NET) translates raw mouse/keyboard/touch/gamepad into neutral
  `PointerInput`/`KeyInput`/`GamepadInput`/text events and queues them on a persistent **`UiHost`**
  (focus, pointer capture, animation clocks live here — *outside* the render graph). Once per frame
  `UiHost.Tick(delta, time, camera, surfaces)` drains → hit-tests → routes → focus/spatial-nav → ticks
  animation → lays out; `UiHost.Render()` produces the per-frame `UiDrawData` surfaces handed to the engine
  via `FrameInputs.UiSurfaces`. Closures pull surfaces; nothing live is captured.
- **Routed events (NAOT-clean):** `RoutedEvent` with dense ids; per-element typed-delegate handler store
  (no dictionaries/`MethodInfo` in the hot path); tunnel(root→target) then bubble(target→root) with `Handled`
  gating + `handledEventsToo`; pooled route chains + pooled args (honor "0 GC in render loop"). Composite
  `Click`/`Tapped` synthesized from pressed/released on the captured element.
- **Hit-testing:** 2D reverse-z walk respecting `IsHitTestVisible`/`Visibility`/clip/transform; 3D path
  unprojects the cursor with the **neutral** [`Camera.ViewProjection`](Loom.Core/Camera.cs) (never the backend
  Y-flipped clip), intersects each in-world quad, maps UV→UI-local, then runs the same 2D walk. **CPU-side, no
  GPU readback** (no `SamplePick` latency). Pointer capture + enter/exit tracking; surface priority =
  captured > topmost screen layer > nearest in-world quad by ray `t`.
- **Focus + navigation:** focus scopes + one global focused element; `Tab`/`Shift-Tab` by `(TabIndex, doc order)`;
  **XYFocus** nearest-in-direction heuristic for gamepad/D-pad/arrows (explicit `XYFocus*` overrides win first;
  scoring weights exposed + a debug overlay since the WinUI constants aren't public). Gamepad: A=invoke,
  B=back, dpad/stick=navigate (debounced).
- **Text input:** `KeyDown` for editing keys + `CharacterReceived` for produced text; `TextBox` caret/selection.
  **IME deferred** — v1 accepts committed chars; define an `ITextInputProvider` host seam now so per-OS IME lands
  later without reworking `TextBox`.
- **Animation:** `Storyboard`/`Timeline`/`DoubleAnimation`/keyframes/easing ticked by `FrameInputs.DeltaTime`,
  pushing into the DP **animation layer** (animation > local). `VisualStateManager.GoToState` drives control
  template states (`Normal`/`PointerOver`/`Pressed`) from routed pointer events. Storyboard target/property and
  visual states are resolved by the generator to typed `ApplyValue` closures — no runtime reflection.

## Milestone roadmap (each phase = a runnable, cross-backend-verified vertical slice)

- **MU0 — Seam + skeleton (riskiest plumbing first).** Core neutral types + `DrawUI`/`EnableUI`/`UiSurfaces`;
  no-op `UiRenderer`/`DX12UiRenderer`; `Loom.UI` skeleton (DP model + precedence ladder incl. animation layer,
  `UIElement`/`FrameworkElement`, Grid/TextBlock/Button, trivial layout); `Loom.UI.SourceGen` compiles a tiny
  XAML subset → constructible tree + `x:Name` fields (no binds); SDK props/targets wire the generator +
  `UpToDateCheck`. **Exit:** a `.xaml` page compiles to a constructible tree; the no-op `DrawUI` seam runs clean
  on both backends. **Tests:** stand up `Loom.UI.Tests` (TUnit + Shouldly) + the NAOT gate (MU0 plumbing), plus
  DP precedence-ladder (incl. the animation rung), trivial-layout, and the first source-gen snapshot tests.
- **MU1 — First pixels.** `ui.slang` (fill + MSDF) → SPIR-V + DXIL; bake one ASCII MSDF fontset; real `UiRenderer`
  (atlas upload + grown buffers + batched scissored draw); `UiRoot` + `Border` + hardcoded `TextBlock`;
  `g.Pass("ui").Blends(screen)`. **Exit:** a rounded rect + crisp MSDF text over the world, pixel-matched
  Vulkan↔DX12 via `LOOM_CAPTURE`. **Tests:** draw-list build (fill + glyph emit, state-run batching, `TextureId`)
  + `FontAtlas`-from-fixture decode via `ImageLoader`.
- **MU2 — Layout + x:Bind + clicks.** Full Grid/StackPanel/Canvas/Border/ContentPresenter + measure/arrange +
  invalidation/no-op; generator x:Bind OneTime/OneWay + event bindings + diagnostics; `UiHost`+pointer
  InputManager, 2D hit-test, bubble pointer events, `Button.Click`. **Exit:** a clickable, data-bound button/list.
  **Tests:** Grid star/auto/pixel + measure/arrange; 2D hit-test; bubble routing order; x:Bind OneWay snapshot.
- **MU3 — Interaction depth + styles.** Tunnel+Handled; keyboard focus + Tab; pointer capture + drag; multiple
  screen layers + popup focus scopes; generator TwoWay/functions/converters/fallback; Styles + StaticResource +
  ResourceDictionary. **Exit:** focus-navigable, two-way-bound, styled form. **Tests:** tunnel + `Handled`
  order; Tab order; pointer capture; TwoWay/converter/fallback snapshots; style precedence rung.
- **MU4 — Templates + animation.** ControlTemplate/DataTemplate + TemplateBinding (nested scoped Bindings +
  generated factories); Storyboard/keyframe/easing on the animation layer; `VisualStateManager`;
  ItemsControl/ListView with data templates. **Exit:** templated, animated controls; templated item list.
  **Tests:** storyboard interpolation/easing/`FillBehavior`; `VisualStateManager` transitions; template factory +
  nested `Bindings` snapshot.
- **MU5 — Full v1.** HarfBuzz shaping + fallback + line-break + rich runs + tofu/warn + bake charset config;
  in-world surface + camera-ray hit-testing; gamepad + XYFocus; touch; HDR-surface color correctness; IME seam
  (committed chars). **Exit:** the reference UI demo — screen-space HUD + interactive in-world panel, mouse +
  keyboard + gamepad + touch, animated + data-bound, pixel-matched across backends. **Tests:** text
  shape/break/wrap + fallback/tofu (fixture); ray→UV hit-test; XYFocus candidate selection; HDR linearization math.

## Verification

The strategy is **graphics-framework-free by design** — the headless-testable invariant (above) means almost the
whole engine is exercised by fast, deterministic CPU tests; only the backend renderer's *pixels* need the GPU.
D8 is the detailed design; the shape:

- **Unit tests** in a new **`Loom.UI.Tests`** project (`net10.0`, **TUnit + Shouldly**; references `Loom.UI` +
  `Loom.Core` + `Loom.Assets`, **no backend ref** — mirroring the [Loom.Tests](Loom.Tests/Loom.Tests.csproj)
  no-Vulkan/DX12 rule). Coverage: DP value precedence (animation > local); Grid star-sizing & measure/arrange
  math; the **draw-list seam** (assert the neutral `UiDrawData` vertex/command stream — the UI analog of asserting
  `BarrierPlan`/`AliasPlan`); 2D + ray→UV hit-testing; routed-event tunnel/bubble ordering + `Handled`; XYFocus
  candidate selection; storyboard interpolation; text shape/break/wrap against a committed font fixture.
- **Source-generator snapshot tests** (Roslyn testing harness, in `Loom.UI.Tests`) asserting the emitted C# for
  representative `.xaml` (the `x:Bind` page, a styled/templated control, a storyboard); diagnostics carry the XAML
  `file:line`.
- **Per milestone (GPU pixels only):** a focused `Loom.Sandbox` demo + a `LOOM_CAPTURE` deterministic frame
  (frozen time/delta) + cross-backend pixel diff (the established loop; [CLAUDE.md](CLAUDE.md) §Agent Behaviour —
  use a subagent to judge captures). This now covers *only* the backend `UiRenderer`/`DX12UiRenderer` — everything
  above it is the deterministic CPU tests above.
- **NativeAOT gate (new):** mark `Loom.UI` `IsAotCompatible`; add a CI `PublishAot` smoke-publish of
  `Loom.Sandbox` treating trim/AOT warnings as errors. No NAOT settings exist in any csproj today — establishing
  this gate is itself an MU0 deliverable.

## Risks & open questions

- **Text is the biggest greenfield subsystem** (no font infra today). Mitigated by prebake + fallback + tofu +
  build-time missing-glyph warnings; HarfBuzzSharp is the proven shaper with bake-time shaping as the purist
  fallback.
- **XAML subset scope** — do *not* promise full WinUI fidelity. Publish a grammar contract: in = elements/attrs,
  `x:Class`/`x:Name`/`x:Key`/`x:DataType`, property-element & content/attached props, `{x:Bind}`/
  `{StaticResource}`/`{ThemeResource}`/`{TemplateBinding}`/`{x:Null}`/`{x:Static}`, `<Style>`/`<Setter>`,
  Data/ControlTemplate, ResourceDictionary + merged; out = `{Binding}`, `x:Phase`/`x:DeferLoadStrategy`,
  conditional XAML, custom markup extensions, `x:Uid`/localization markup.
- **NAOT discipline** — guard against accidental reflection (enum attrs → typed `switch`, not `Enum.Parse`;
  converter factories, not `Activator`) and boxing in the DP store (the typed generic fast path is the mitigation).
- **Gamepad XYFocus** heuristic needs tuning (WinUI constants are private) — ship tunable weights + a debug overlay.
- **In-world occlusion** — the analytic ray test ignores scene depth; v1 accepts "ghostly" UI behind geometry;
  opt-in `SamplePick` occlusion (1–2 frame latency) is a later escape hatch.
- **IME** deferred to a post-v1 per-OS lift behind `ITextInputProvider`.

## Tracking

This is a new track comparable in size to the whole M11–M16 roadmap. When work starts, add a new **Stage** to
the GitHub Projects board (#6, `AtomicBlom`) for the UI track (e.g. `MU0`…`MU5`, orange until shipped) and break
each milestone into task cards, per the board conventions in [CLAUDE.md](CLAUDE.md). MU0 carries the SDK/generator
plumbing + the NAOT gate + the `Loom.UI.Tests` scaffold (TUnit + Shouldly) + the MSDF font fixture + the
headless-testable invariant (D8); treat the XAML-subset grammar as a `Decision` card to resolve and publish before MU2.

---

# Part II — Detailed Design

Everything above is the executive plan. This part is the design bible: concrete type sketches, the generator
pipeline, worked codegen, the MSDF bake schema, hit-testing math, and the routed-event model. Type names and
signatures are *indicative* — they show shape and intent, not a frozen API. All code obeys repo conventions
(file-scoped namespaces, tabs, Allman, `record struct` for value types, expression-bodied members).

## D0. Layering recap (where each type lives)

```
Loom.Core   (neutral, no GPU types)   UiVertex · UiPrimKind · UiDrawCommand · UiDrawData · UiSurfaceId
                                       + IEngine.EnableUI · IRenderContext.DrawUI · FrameInputs.UiSurfaces
   ▲
Loom.UI     (neutral, refs Core+Assets) DependencyObject/Property · UIElement→FrameworkElement · controls ·
                                       layout · UiRoot · UiDrawList builder · routed events · focus/XYFocus ·
                                       hit-testing (2D + ray) · UiHost · animation · TextLayoutEngine · FontAtlas
   ▲  (build-time only — analyzer + content; NOT a runtime ref)
Loom.UI.SourceGen  (netstandard2.0 analyzer)   .xaml → .g.cs (tree) + .Bindings.g.cs (typed x:Bind/events/storyboards)
Loom.UI.Text.Bake  (net10.0 build tool)         *.fontset.json → MSDF atlas PNG + metrics JSON
   ▲
Loom.Vulkan / Loom.DX12  (backends)    UiRenderer / DX12UiRenderer (clones of the ImGui renderers)
   ▲
Loom.Sandbox  (host)                   Silk.NET input → neutral events → UiHost; declares the ui pass(es)
```

Cardinal invariant restated: the only Core additions are **neutral managed data** (no `Vk*`/`D3D12*` types);
`Loom.UI` reaches the GPU only through `RenderGraph.Define` / `IEngine` / `IRenderContext`, exactly as a demo does.

Second invariant — **headless-testable by construction** (load-bearing; see D8): `UiHost.Tick(...)`,
`UiRoot.LayoutPass(...)` and `UiRoot.Render()` must run to completion with **no `IEngine`, no backend, no
window** — input crosses in as neutral events, the product is neutral `UiDrawData`, and the engine is touched
only at the final `ctx.DrawUI` upload. Coupling layout / input / routing / animation / draw-list build to a
live engine handle is a design violation; it is precisely what keeps nearly the whole engine unit-testable with
no GPU, the way the Describe/Execute layers already are.

---

## D1. Compile-time: object model & source generator

### D1.1 NAOT dependency-property model

Classic WinUI `DependencyProperty.Register(string, typeof(T), typeof(Owner), metadata)` is `Type`-keyed,
string-named, and reflection-dispatched. We keep the *semantics* and replace the *mechanism*: dense integer
slots, typed delegates, no `MethodInfo`, boxing-free value-type fast path.

```csharp
namespace Loom.UI;

public abstract class DependencyProperty
{
	public int    GlobalIndex  { get; }          // dense slot id assigned at registration (D1.2)
	public string Name         { get; }          // diagnostics only — never a lookup key
	public DependencyPropertyKind Kind { get; }  // Normal | Attached
	public InvalidationFlags Invalidates { get; }// None | Measure | Arrange | Render
}

public sealed class DependencyProperty<TOwner, TValue> : DependencyProperty
	where TOwner : DependencyObject
{
	public PropertyMetadata<TOwner, TValue> Metadata { get; }
	internal TValue DefaultValue => Metadata.DefaultValue;   // typed, no boxing
}

public sealed class PropertyMetadata<TOwner, TValue>
{
	public TValue DefaultValue { get; init; } = default!;
	public Action<TOwner, TValue, TValue>? PropertyChanged { get; init; } // typed (old,new); no boxing
	public Func<TOwner, TValue, TValue>?    Coerce          { get; init; }
	public InvalidationFlags                Invalidates     { get; init; }
}

[Flags] public enum InvalidationFlags { None = 0, Measure = 1, Arrange = 2, Render = 4 }
public enum DependencyPropertyKind { Normal, Attached }
```

A control declares a property with a static field + typed accessor (the `PropertyChanged` lambda is `static`,
captured once at registration — no per-instance delegate, no reflection):

```csharp
public partial class TextBlock : FrameworkElement
{
	public static readonly DependencyProperty<TextBlock, string> TextProperty =
		DependencyProperty.Register<TextBlock, string>(nameof(Text),
			new PropertyMetadata<TextBlock, string>
			{
				DefaultValue = "",
				Invalidates  = InvalidationFlags.Measure,
				PropertyChanged = static (o, _, _) => o.OnTextChanged(),
			});

	public string Text { get => GetValue(TextProperty); set => SetValue(TextProperty, value); }
}
```

`GetValue<T>(DependencyProperty<TOwner,T>)` / `SetValue<T>` are generic, so the local-value fast path never
boxes value types — the chief AOT-perf mitigation (a boxing DP store would trade reflection cost for GC cost).

### D1.2 Registration & the effective-value store

Each `Register` call records a descriptor; a generated `[ModuleInitializer]` (the
[`VulkanBackend`](Loom.Vulkan/VulkanBackend.cs) self-registration pattern) walks the ordered descriptor list
and assigns dense `GlobalIndex` values. Dense indices let `DependencyObject` use a small int-keyed store
(sparse array / open-addressed map) instead of a reference-hashed `Dictionary<DependencyProperty, …>`.

**Value precedence (highest first), with the animation layer present from day one:**

```
Animation  >  Local  >  Style  >  Inherited (inheritable props only)  >  Default
```

A `SetValue` recomputes the top of the ladder via a fixed `switch` over sources (no reflection), fires the
typed `PropertyChanged` thunk only if the *resolved* value changed, and ORs the property's `InvalidationFlags`
into the element's dirty state — scheduling `InvalidateMeasure/Arrange/Render` up the tree.
`SetValue` writes the **Local** rung and must never clobber an active **Animation** value (MU4 correctness).

```csharp
public abstract class DependencyObject
{
	public  TValue GetValue<TOwner, TValue>(DependencyProperty<TOwner, TValue> dp) where TOwner : DependencyObject;
	public  void   SetValue<TOwner, TValue>(DependencyProperty<TOwner, TValue> dp, TValue value) where TOwner : DependencyObject;
	internal void  SetAnimatedValue(DependencyProperty dp, object boxed);   // animation rung (MU4)
	internal void  ClearAnimatedValue(DependencyProperty dp);               // FillBehavior.Stop / storyboard end
}
```

### D1.3 Attached properties

Same `DependencyProperty<TOwner,TValue>` with `Kind = Attached`; the declaring type (`Grid`) is the "owner,"
the target is any `DependencyObject`, and the value lives on the **target's** store under the shared
`GlobalIndex` (one index space ⇒ no collisions). The generator resolves `Grid.Row="2"` to a direct
`Grid.SetRow(child, 2)` call — never string-to-setter reflection. For the hot layout path, panels read attached
layout values (`Grid.Row`, `Canvas.Left`) from a small per-element side-struct rather than the general store,
to avoid any map lookup during measure/arrange.

### D1.4 The generator pipeline

A **single** `IIncrementalGenerator`. `.xaml` files arrive as `AdditionalFiles` (registered by
`Loom.UI.props`); the generator parses each and emits two partial files per `x:Class`:

```
 .xaml  ──(AdditionalText)──►  Loom.UI.SourceGen (IIncrementalGenerator)
                                   │  uses Compilation.GetTypeByMetadataName(x:DataType)
                                   ▼
              foo.g.cs                         foo.Bindings.g.cs
   partial class Foo {                  internal sealed class Foo_Bindings {
     typed x:Name fields                  Initialize / Update / StopTracking
     InitializeComponent()  ───────────►  typed get/set per bind id
       (generated `new`, value sets,      INPC subscribe/unsubscribe (switch on
        event AddHandler, Bindings init)    PropertyName literals → Update_N)
   }                                        converters, fallback/target-null, events
```

Why a single generator (vs the originally-considered hybrid task+generator): it runs inside the compiler with
the semantic model already populated, so it resolves `x:DataType` and `x:Bind` paths to typed accessors directly;
one compile pass; live IntelliSense + Hot Reload on `.xaml` edits; and it sidesteps the netstandard2.0 MSBuild-task
loading hazard entirely. Trade-off accepted: tree-construction codegen is a little more verbose in a generator,
and there's no natural home for binary sidecars (fine for an all-C# v1).

Incrementality: the pipeline keys on each `.xaml`'s text + the referenced `x:DataType` symbol, so an untouched
page re-emits nothing. Diagnostics carry the XAML `file:line` (parsed span) so binding/type errors squiggle in
the `.xaml`, not in generated code. The generator errors clearly on any construct outside the published subset (D1.8).

### D1.5 x:Bind change tracking

The generated `Bindings` class is a WinUI `IComponentConnector`-shaped contract:

- `Initialize(root, data)` — store typed root + data, run `OneTime` assignments, `Subscribe()`, `Update()`.
- `Update()` — push every `OneWay`/`OneTime` source value to its target via the **typed** setter.
- `Subscribe()` / `StopTracking()` — one `PropertyChangedEventHandler` per source object; the handler `switch`es
  on `e.PropertyName` against **emitted string literals** and calls the specific `Update_<id>()`. No `PropertyInfo`.
- **Deep paths** (`User.DisplayName`): subscribe at each hop; on an intermediate change, unsubscribe the old
  instance, re-subscribe the new, re-evaluate the leaf — emitted explicitly.
- **TwoWay**: also subscribe the target control's DP-changed event; on change call the source's typed setter,
  guarded by a re-entrancy flag.
- **Function paths** (`{x:Bind vm.Format(Count)}`): resolve method + arg symbols, emit a typed call, re-evaluate
  when any arg source raises INPC.
- **Modes**: `{x:Bind}` defaults to `OneTime` (WinUI semantics); `OneWay`/`TwoWay` explicit.
- **Converters**: `Converter={StaticResource Foo}` → a generated singleton/factory of `IValueConverter`; emit
  `converter.Convert(typedValue, …)`.
- **Fallback / TargetNull**: inline literals in the typed path (`x ?? targetNull`; null mid-path short-circuits to fallback).

### D1.6 Styles, templates, resources at compile time

Principle: **every XAML construct becomes C#; instantiation is `new` / a generated `Func<>`, never
`Activator.CreateInstance` over a reflected `Type`.**

- `<Style>` → a generated builder producing a `Style` whose setters are typed apply-delegates
  (`static t => ((Button)t).Background = …`); `Setter Property="Background"` resolves at generate time to the
  `DependencyProperty` static field. Applying writes the **Style** precedence rung; `BasedOn` chains by reference.
- `ControlTemplate` / `DataTemplate` → a generated `TemplateFactory`: `Func<FrameworkElement>` (control) or
  `Func<object, FrameworkElement>` (data) that `new`s the subtree and wires a *nested* `Bindings` scoped to the
  template's `x:DataType`. Template-internal `x:Bind` is typed exactly like a page.
- `{TemplateBinding Foo}` → a `OneWay` typed wire from the templated parent's DP to the child's DP at instantiation.
- `ResourceDictionary` → generated holder with typed accessors keyed by resource key; merged dictionaries chain by
  reference; each resource is a lazily-constructed singleton via a generated factory.
- `{StaticResource Key}` → resolved at compile time to a direct accessor call when statically in scope; falls back
  to a generated `Dictionary<string, Func<object>>` for dynamic/merge cases — no reflection in either path.
- `{ThemeResource Key}` → like StaticResource but re-fetched on theme change via a generated subscription
  (v1 may treat it as StaticResource + a manual re-apply; full theme dictionaries are a later slice).
- `x:DataType` → the type the page/template's `{x:Bind}` paths bind against (required for compiled bindings in
  templates, matching WinUI).

> **NAOT checkpoint:** no `Type`-keyed map, no `Activator.CreateInstance`, no `MethodInfo` anywhere in the
> resource/style/template/binding path. Every "create" is a generated `new`/`Func<>`, every "set" a typed setter,
> every VM read a direct member access.

### D1.7 Worked example

**Input — `MainPage.xaml`:**

```xml
<Page x:Class="Loom.Sandbox.UI.MainPage"
      xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
      xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
      x:DataType="vm:MainViewModel" xmlns:vm="using:Loom.Sandbox.UI">
  <Grid>
    <TextBlock x:Name="Title" Text="{x:Bind ViewModel.Greeting, Mode=OneWay}" />
    <Button x:Name="GreetBtn" Content="Greet" Click="{x:Bind OnGreetClick}" />
  </Grid>
</Page>
```

**Emitted — `MainPage.g.cs` (tree construction):**

```csharp
// <auto-generated/> by Loom.UI.SourceGen
#nullable enable
namespace Loom.Sandbox.UI;
using Loom.UI; using Loom.UI.Controls;

partial class MainPage
{
	private TextBlock Title = null!;
	private Button    GreetBtn = null!;
	private MainPage_Bindings? _bindings;
	public  MainViewModel ViewModel { get; set; } = null!;   // x:Bind root

	private void InitializeComponent()
	{
		var grid = new Grid();                                 // generated new, never Activator.CreateInstance
		Title    = new TextBlock();                grid.Children.Add(Title);
		GreetBtn = new Button { Content = "Greet" }; grid.Children.Add(GreetBtn);
		this.Content = grid;
		_bindings = new MainPage_Bindings(this);
		_bindings.Initialize(this, this.ViewModel);
	}
}
```

**Emitted — `MainPage.Bindings.g.cs` (typed glue):**

```csharp
// <auto-generated/> by Loom.UI.SourceGen
#nullable enable
namespace Loom.Sandbox.UI;
using System.ComponentModel; using Loom.UI;

internal sealed class MainPage_Bindings
{
	private readonly MainPage _root;
	private MainViewModel _data = null!;
	public MainPage_Bindings(MainPage root) => _root = root;

	public void Initialize(MainPage root, MainViewModel data)
	{
		_data = data;
		_root.GreetBtn.Click += _root.OnGreetClick;            // signature-checked at generate time
		Subscribe();
		Update();
	}

	private void Update_0() => _root.Title.Text = _data.Greeting ?? string.Empty;  // typed, no boxing/reflection
	public  void Update()   => Update_0();

	private void Subscribe()
	{
		if (_data is INotifyPropertyChanged inpc) inpc.PropertyChanged += OnDataPropertyChanged;
	}
	public void StopTracking()
	{
		if (_data is INotifyPropertyChanged inpc) inpc.PropertyChanged -= OnDataPropertyChanged;
		_root.GreetBtn.Click -= _root.OnGreetClick;
	}

	private void OnDataPropertyChanged(object? sender, PropertyChangedEventArgs e)
	{
		switch (e.PropertyName)                                // literal compare, NOT reflection
		{
			case nameof(MainViewModel.Greeting): case null: case "": Update_0(); break;
		}
	}
}
```

`OnGreetClick(object, RoutedEventArgs)` is the user's hand-written code-behind partial; the generator verifies
its signature against `Button.Click` and emits a diagnostic on the XAML line otherwise.

### D1.8 Published XAML subset (the hard contract)

**In:** elements/attributes; `x:Class` / `x:Name` / `x:Key` / `x:DataType`; property-element syntax; content +
attached properties; markup extensions `{x:Bind}` / `{StaticResource}` / `{ThemeResource}` / `{TemplateBinding}`
/ `{x:Null}` / `{x:Static}`; `<Style>` / `<Setter>`; `DataTemplate` / `ControlTemplate`; `ResourceDictionary`
+ merged dictionaries; `VisualStateManager` groups/states (MU4).

**Out (v1):** `{Binding}` (reflection-based; deliberately excluded); `x:Phase`; `x:DeferLoadStrategy`;
conditional XAML; custom (user-defined) markup extensions; `x:Uid` / localization markup; `RelativeSource`;
implicit data templates by type. The generator emits a clear "outside the Loom XAML subset" diagnostic for these.

---

## D2. Runtime: visual tree, layout, rendering

### D2.1 Element hierarchy

```
UIElement → FrameworkElement
   ├─ Panel (Children) ── Grid · StackPanel · Canvas
   ├─ Border · ContentPresenter · ItemsPresenter
   ├─ TextBlock (rich runs — D3) · Image
   └─ Control ── ContentControl→Button · ItemsControl→ListView
```

```csharp
public abstract class UIElement : DependencyObject
{
	public Size DesiredSize { get; private set; }     // result of Measure
	public Rect LayoutRect  { get; private set; }     // result of Arrange (parent space)
	internal bool MeasureDirty = true, ArrangeDirty = true;

	public float      Opacity = 1f;
	public Matrix3x2  RenderTransform = Matrix3x2.Identity;
	public bool       ClipToBounds;
	public Visibility Visibility = Visibility.Visible;  // Collapsed ⇒ zero measure, no hit
	public bool       IsHitTestVisible = true;
	internal UIElement? Parent;

	public void Measure(Size available);                // skips if clean & available unchanged
	public void Arrange(Rect finalRect);
	protected virtual Size MeasureOverride(Size available) => Size.Zero;
	protected virtual Size ArrangeOverride(Size finalSize)  => finalSize;

	public void InvalidateMeasure();  // mark + walk up + enqueue on UiRoot
	public void InvalidateArrange();
	public void InvalidateVisual();   // render-only dirty (no relayout)

	internal abstract void OnRender(UiDrawList dl);
	internal virtual  UIElement? HitTest(Vector2 localPoint);   // reverse z, respect clip + IsHitTestVisible
}

public abstract class FrameworkElement : UIElement
{
	public float Width = float.NaN, Height = float.NaN;        // NaN = Auto
	public float MinWidth, MinHeight, MaxWidth = float.PositiveInfinity, MaxHeight = float.PositiveInfinity;
	public Thickness Margin;
	public HorizontalAlignment HorizontalAlignment = HorizontalAlignment.Stretch;
	public VerticalAlignment   VerticalAlignment   = VerticalAlignment.Stretch;
	public string? Name;
}
```

**v1 control set:** `TextBlock`, `Button`, `Image`, `Border`, `ItemsControl`/`ListView`; panels `Grid`,
`StackPanel`, `Canvas`; hosts `ContentPresenter`, `ItemsPresenter` — the minimum that proves the two-pass
layout, content/items templating, and the three render-primitive families (fill, text, image).

### D2.2 Two-pass measure/arrange (WinUI semantics)

- **Measure(available):** `FrameworkElement.Measure` deflates by `Margin`, clamps to Min/Max + explicit W/H,
  calls `MeasureOverride`, inflates by Margin, re-clamps → `DesiredSize`.
- **Arrange(finalRect):** applies alignment (Stretch fills the slot; else the child takes `DesiredSize` aligned
  within the slot), calls `ArrangeOverride`, records `LayoutRect`.

### D2.3 Panels

- **Grid** (the only non-trivial one): resolve column/row tracks — Pixel fixed, Auto = max child desired, Star =
  leftover distributed proportionally after Auto/Pixel are subtracted — measure children against cell constraints,
  arrange with `ColumnSpan`/`RowSpan`. Straight WinUI star math in v1.
- **StackPanel:** stacking axis = ∞ in measure; sum desired on that axis, max on the cross axis; arrange end-to-end.
- **Canvas:** measure children ∞×∞, desire `Size.Zero`; arrange at attached `(Left, Top)` with `DesiredSize`.
- **Border / ContentPresenter:** deflate by Padding/BorderThickness, measure the single child, desire child +
  padding + border.

### D2.4 Invalidation & UiRoot

`UiRoot` is one per UI surface. Layout-affecting setters call `InvalidateMeasure()`; pixel-only setters
(brush color, `Opacity`) call `InvalidateVisual()`. A **frame-level no-op** short-circuits a fully-clean frame.

```csharp
public sealed class UiRoot
{
	public UIElement? Content;
	public float LayoutScale = 1f;        // DIP→pixel (window DPI, or in-world texel density)
	public void      LayoutPass(Size availablePixels);  // re-measure/arrange highest dirty ancestors
	public UiDrawData Render();           // walk tree → build neutral draw data (reuses last if clean)
}
```

### D2.5 2D render-data model (`Loom.Core` neutral types) + draw-list builder

Retained tree, draw-list rebuilt **only on dirty frames**; the walk resolves transform/clip/opacity stacks into
a pre-transformed vertex/command stream (the ImGui ergonomics without re-walking a clean tree).

```csharp
namespace Loom.Core;   // neutral — no graphics-API types

[StructLayout(LayoutKind.Sequential)]
public readonly record struct UiVertex(
	Vector2 Position,   // surface pixels (post-transform, post-DPI)
	Vector2 Uv,         // atlas UV (glyph/image) or local rect-space for SDF rounding
	uint    Color,      // premultiplied RGBA8
	Vector4 Params);    // per-prim: rounded-rect radii / border half-width / SDF range

public enum UiPrimKind : byte { Fill, RoundedRect, Border, Glyph, Image }

public readonly record struct UiDrawCommand(
	UiPrimKind Kind, int TextureId, Rect ClipRect,
	int IndexOffset, int IndexCount, int VertexOffset);

public sealed class UiDrawData             // rebuilt per dirty frame; pooled buffers
{
	public ReadOnlyMemory<UiVertex>      Vertices;
	public ReadOnlyMemory<ushort>        Indices;     // ushort + VtxOffset for >64k (cf. ImGuiRenderer)
	public ReadOnlyMemory<UiDrawCommand> Commands;
	public Vector2 SurfaceSize;                        // for the pixel→clip push
	public bool    IsHdrSurface;                       // drives color linearization (D2.8/§HDR)
	public uint    Revision;                           // damage / no-op detection
}

public readonly record struct UiSurfaceId(int Value);  // overlay + N in-world quads each have one
```

The `UiDrawList` builder (in `Loom.UI`) owns the transform/clip/opacity **stacks** and flattens them:

```csharp
public sealed class UiDrawList   // analog of ImDrawList; pooled across frames
{
	void PushTransform(in Matrix3x2 m); void PopTransform();   // composed, baked into vertices
	void PushClip(in Rect r);           void PopClip();        // intersected; sets UiDrawCommand.ClipRect
	void PushOpacity(float a);          void PopOpacity();     // multiplied into vertex alpha
	void AddRect(in Rect r, uint color, float cornerRadius = 0);
	void AddBorder(in Rect r, float thickness, uint color, float cornerRadius = 0);
	void AddImage(int textureId, in Rect dst, in Rect uv, uint tint);
	void AddGlyphRun(int atlasPage, ReadOnlySpan<PositionedGlyph> glyphs, uint color, float screenPxRange);
}
```

**Opacity in v1 folds into vertex alpha** (correct for non-overlapping content, the common HUD case). True
WinUI group-opacity (a subtree composited as one bitmap, then faded) reuses the in-world render-to-texture
machinery (D4.2): render the subtree to a transient target, composite with one `AddImage` at the group alpha —
designed-for, not built in v1.

### D2.6 Batching — atlas-everything + state-run batching

Three texture sources share one atlas-addressing scheme: solid fills/rounded-rects/borders use `TextureId = 0`
(a 1×1 white texel); glyphs sample an MSDF page; images sample a UI image atlas or an in-world source target.
A new `UiDrawCommand` is emitted only when `(Kind-family, TextureId, ClipRect)` changes — within a run, thousands
of rects/glyphs are one indexed draw. Strictly better than ImGui (which flushes on every clip/texture change)
where it matters; identical where it doesn't; reuses the exact GPU upload path (concat vertices/indices, scissor
per command, `CmdDrawIndexed`).

### D2.7 `ui.slang`

One module: `vertexMain` + a `fragmentMain` that switches on a flat `uint kind` interpolant.
- **Fill / RoundedRect / Border** — analytic rounded-box SDF in local rect space (radii/half-width from
  `UiVertex.Params`), AA via `fwidth` coverage. No texture.
- **Glyph (MSDF)** — `median(rgb)` distance, `screenPxRange`-scaled smoothstep coverage (D3.4). Samples a font page.
- **Image** — sample atlas / in-world source with tint.

Vertex push = the pixel→clip `scale+translate` ImGui uses, plus `float linearize` (HDR/sRGB) and the per-batch
`screenPxRange`. Two pipelines per color space (alpha-over + optional additive for glow). Compiled to SPIR-V +
signed DXIL by the existing dual-emit toolchain; driven by the bespoke `UiRenderer` (custom blend + dynamic
scissor), like ImGui, rather than the generic technique path.

### D2.8 DPI / coordinate space

Layout in **device-independent pixels (DIPs)**; the render walk multiplies positions by `UiRoot.LayoutScale`
when emitting primitives. Overlay `LayoutScale` = window DPI/96; in-world `LayoutScale` = author-chosen quad
texel density (decoupled from the window). MSDF is resolution-independent, so there is **no per-DPI re-bake**.
Mid-session per-monitor DPI changes set `LayoutScale` + invalidate the root.

---

## D3. Text (MSDF)

| Concern | When | Tool / lib |
|---|---|---|
| Font subsetting (charset → glyph set) | build | bake tool reads charset config |
| MSDF glyph generation + atlas packing | build | **msdf-atlas-gen** (C++, MIT), bundled hermetically |
| Glyph metrics + kerning + UVs → JSON | build | msdf-atlas-gen `-json` + our merge |
| Packaging (atlas PNG + JSON → embedded/Content) | build | mirrors shader sidecars |
| Text **shaping** (runs → positioned glyphs) | runtime | **HarfBuzzSharp** (MIT) |
| Font **fallback** chain | runtime | our `FontFallbackChain` |
| Line breaking / wrapping / rich runs | runtime | our `TextLayoutEngine` (UAX-14-lite v1) |
| Glyph → UV lookup, draw | runtime | our `FontAtlas` (loads baked JSON + PNG via `ImageLoader.Decode`) |

### D3.1 Build-time bake tool & `*.fontset.json`

`Loom.UI.Text.Bake` is a build-time console tool (never shipped) sitting beside the Slang toolchain in
`build/Loom.Sdk/`. An MSBuild target (`LoomCompile/RegisterFonts`) mirrors `LoomCompileShaders`/`LoomRegisterShaders`:
inputs = font files + a charset config; outputs = atlas PNG page(s) + metrics JSON; registered `EmbeddedResource`
(engine default font) or `Content` (app fonts).

```json
{
  "name": "ui-default",
  "fonts": [
    { "path": "Inter.ttf",          "ranges": ["Basic Latin", "Latin-1 Supplement", "General Punctuation"] },
    { "path": "Inter-Bold.ttf",     "weight": "Bold" },
    { "path": "NotoSansCJK.otf",    "ranges": ["CJK subset (driven by the localization string table)"], "role": "fallback" },
    { "path": "NotoSansArabic.ttf", "script": "Arabic", "role": "fallback" }
  ],
  "pxRange": 4, "glyphSize": 48, "atlasPageSize": 2048
}
```

Outputs per fontset: `ui-default.N.png` (RGB MSDF pages) and `ui-default.font.json` (per-glyph page+UV rect,
plane bounds in EM, advance, bearings; font metrics; `pxRange`; the glyph-id↔codepoint subset cmap) — the text
analog of the Slang reflection sidecar. The tool also emits/keeps a subset `.ttf` for HarfBuzz so glyph ids
match the baked JSON.

### D3.2 Runtime shaping & layout

`TextBlock` holds rich inline runs (`Run { text, FontKey, size, weight, color }`, plus `LineBreak`,
`InlineImage`). `TextLayoutEngine`:
1. **Itemize** by script + direction + font (BiDi via UAX-9; v1 LTR + basic RTL, complex reordering behind the API).
2. **Shape** each item with **HarfBuzzSharp** → positioned glyph ids + advances + clusters (ligatures, marks,
   complex-script reordering).
3. **Line-break** (UAX-14 opportunities; v1 = whitespace + mandatory breaks + simple CJK) and **wrap** to the
   measure constraint.
4. **Emit** `AddGlyphRun(...)` per run during render.

`TextBlock.MeasureOverride` runs shaping+breaking against the available width and returns the block size — text
participates in two-pass layout like any element. Shaping is **cached** by `(text, fontKey, size, constraintWidth)`,
so a clean frame does no shaping (feeds the D2.4 no-op).

### D3.3 Coverage policy (prebaked MSDF vs large/dynamic glyph sets)

- **Subset by charset config at build time** — UI/HUD text is overwhelmingly a known, bounded charset; the
  localization string table drives the config, so CJK ships only used glyphs.
- **Fallback chain** — runtime glyph lookup walks a `FontFallbackChain` (primary → bold → CJK → Arabic → …),
  each link a separately-baked fontset.
- **Unbaked glyph** — three tiers in order: (1) `.notdef`/tofu box from a guaranteed-present glyph (never a crash);
  (2) **build-time warning** from the bake tool for any string-table codepoint with no covering font (fail-loud,
  like `GraphBuilder` validation); (3) *(v2, opt-in)* runtime dynamic MSDF bake-on-miss on a background thread —
  **off by default, not v1** (it reintroduces a native dep + a GPU upload in the loop).

Why MSDF over a bitmap atlas (ImGui) or runtime rasterization: resolution-independence — one small atlas renders
crisp at any DPI and any in-world texel density, the exact overlay-and-in-world dual requirement. MSDF's soft
corners under extreme minification are irrelevant at HUD text sizes.

### D3.4 MSDF fragment math (`ui.slang`)

```
float screenPxRange = pxRange * (onScreenEmPx / bakedGlyphSizePx);  // computed CPU-side during the walk
float3 s   = atlas.Sample(uv).rgb;
float  d   = median(s.r, s.g, s.b) - 0.5;
float  cov = clamp(d * screenPxRange + 0.5, 0, 1);
color.a   *= cov;
```

### D3.5 Libraries (NAOT-compatible, license-vetted)

| Need | Choice | License | NAOT | Rationale |
|---|---|---|---|---|
| MSDF generation (build) | **msdf-atlas-gen** | MIT | n/a (build only) | Canonical generator; emits atlas + JSON + packing. Bundled like `slangc`. |
| Shaping (runtime) | **HarfBuzzSharp** | MIT binding | Yes — native lib + thin P/Invoke, no reflection | Industry-standard shaper; well-maintained on .NET; cross-platform. *Native ≠ reflection.* |
| BiDi / line-break (runtime) | small in-repo UAX-14/UAX-9 | MIT | Yes (pure managed) | Table-driven; keeps v1 dep-light. |
| Atlas PNG decode | **`ImageLoader.Decode`** (`Loom.Assets`; StbImageSharp-backed) | MIT | Yes (pure managed) | Shipped in M11 → neutral `TextureData`/`MipLevel`; already a demo/test dep, no new dep. |

Native-shaper tension: the bar is *no reflection in hot paths*, not *no native code* (the engine already ships
native Vulkan/DX12/cimgui + Slang/DXC at build). HarfBuzzSharp fits that mold. NAOT-purist / fixed-string-table
escape hatch: **bake-time shaping** — run HarfBuzz over the known string table at build and emit pre-shaped runs,
leaving the runtime with no shaper. Documented; not v1.

---

## D4. Compositing into the render graph

### D4.1 Screen-space overlay (native res, after upscale, blends backbuffer)

```csharp
g.Pass("upscale").Samples(graded).Writes(screen).Draw(ctx => ctx.FullScreen("blit", graded));
g.Pass("ui").Blends(screen).Draw(ctx => ctx.DrawUI(UiSurface.Hud));  // native res, blends backbuffer
DemoGraph.Hud(g, screen);   // ImGui debug overlay still available on top
g.Present(screen);
```

A `DemoGraph.Ui(g, screen, surfaceId)` helper wraps that line, mirroring `DemoGraph.Hud`. `.Blends` is the
correct verb — load + alpha-over, preserves the backbuffer (the `.Writes`-vs-`.Blends` distinction the engine
relies on).

### D4.2 In-world UI surface (own domain target → sampled on a 3D quad)

Reuses the shadow-map / bloom-pyramid texture-reuse pattern verbatim:

```csharp
var panelDomain = g.Domain("ui_panel", 512, 256);
var panelTex    = panelDomain.Color("ui_panel_color", Format.Rgba8Srgb);   // UI authored in sRGB

g.Pass("ui.panel")
	.Writes(panelTex)
	.Draw(ctx => ctx.DrawUI(UiSurface.WorldPanel));

g.Pass("gbuffer")
	.Writes(albedo, normal, position).WritesDepth(depth)
	.Samples(panelTex)                                   // derives COLOR_ATTACHMENT→SHADER_READ transition
	.Draw(ctx =>
	{
		ctx.Draw(scene.Cube, "gbuffer");
		ctx.Draw(scene.PanelQuad, "ui_in_world");        // technique samples panelTex at binding 0
	});
```

The panel target is **transient** by default (aliases away after its last reader, like bloom mips); a slowly-
updating surface (a minimap) uses `domain.Persistent(...)`. Interactive in-world UI = the host ray-casts the
cursor onto the quad, maps the hit to UV → panel DIPs, and feeds it to that surface's `UiRoot` CPU hit-test (D5.4).

### D4.3 Resource-model fit

Overlay adds no target (`.Blends` the backbuffer). In-world adds one `domain.Color` per surface (transient or
persistent). Both are first-class in the describe layer today — the UI adds **no new resource concept**, only
the `ctx.DrawUI` verb + the per-frame `UiDrawData` surface channel.

### D4.4 HDR / sRGB correctness

The backbuffer may be scRGB-HDR (`Swapchain.HdrActive` → `ctx.HdrOutput`). UI colors are authored sRGB but the
overlay blends a post-tonemap native backbuffer that under HDR is extended-range scRGB, so naive sRGB8 vertices
would blend wrong. `ui.slang` carries an `IsHdrSurface` flag (from `UiDrawData.IsHdrSurface`, set from
`ctx.HdrOutput`); when set, the shader linearizes sRGB UI colors to scRGB and premultiplies so alpha-over is
correct (mirrors how tonemap takes the HDR flag). In-world panels render into `Rgba8Srgb` and sample correctly
regardless. Open: opt-in "above paper-white" (glowing HUD) brushes under HDR.

---

## D5. Input, interaction, animation

### D5.1 Pipeline & UiHost (the pure-closure split)

Input is per-frame mutable host state, so it crosses to the engine through `FrameInputs` (immutable, pull-only —
preserving the closure invariant). But UI also needs **persistent** state (focus, pointer capture, animation
clocks) that a fresh-per-frame `FrameInputs` deliberately doesn't model. Resolution: persistent state lives in a
host-owned **`UiHost`** *outside* the render graph; only the already-computed neutral draw data crosses in. This
is exactly the ImGui split (persistent context host-side; only `ImDrawData*` crosses).

```csharp
public sealed class UiHost
{
	public UIElement? RootContent { get; set; }
	public InputManager  Input { get; }     // routing, capture, over-tracking (persistent)
	public FocusManager  Focus { get; }
	public AnimationClock Clock { get; }     // ticks storyboards

	// host pushes normalized events between frames:
	public void QueuePointer(in PointerInput e);
	public void QueueKey(in KeyInput e);
	public void QueueText(ReadOnlySpan<char> utf16);
	public void QueueGamepad(in GamepadInput e);

	// once per frame, after queueing, before engine.Render:
	public void   Tick(double deltaSeconds, float time, in Camera camera, in UiSurfaceSet surfaces);
	public void   Render();                  // produce per-surface UiDrawData → FrameInputs.UiSurfaces
}
```

`Tick` drains the queue → hit-tests → routes → focus/spatial-nav → advances animation clocks → lays out.
`camera` feeds the 3D ray; `UiSurfaceSet` enumerates screen-space layers + in-world quads (D5.4).

### D5.2 Neutral input events + host call sequence

Device-neutral, WinUI-shaped (no Silk types leak into `Loom.UI`):

```csharp
public readonly record struct PointerInput(uint PointerId, PointerDeviceType Device, Vector2 Position,
	PointerUpdateKind Update, PointerButtons Buttons, float Pressure, Vector2 Wheel, ulong TimestampMicros);
public readonly record struct KeyInput(VirtualKey Key, bool Down, bool Repeat, ModifierKeys Modifiers, ulong TimestampMicros);
public readonly record struct GamepadInput(GamepadButtons Buttons, Vector2 LeftStick, Vector2 RightStick,
	float LeftTrigger, float RightTrigger, ulong TimestampMicros);
```

Host (Sandbox) wiring, alongside the existing `window.CreateInput()`:

```csharp
using var input = window.CreateInput();
var uiHost = new UiHost { RootContent = BuildRootVisual() };
engine.EnableUI();

mouse.MouseMove += (_, p) => uiHost.QueuePointer(/* Mouse move … */);
mouse.MouseDown += (_, b) => uiHost.QueuePointer(/* LeftButtonPressed … */);
keyboard.KeyChar += (_, c) => uiHost.QueueText(stackalloc[] { c });      // text entry
// gamepad polled each frame from input.Gamepads[0] → QueueGamepad

window.Render += delta =>
{
	var surfaces = new UiSurfaceSet(window.FramebufferSize, worldQuads);
	uiHost.Tick(delta, elapsed, camera, surfaces);   // drain+route+focus+animate+layout
	uiHost.Render();                                  // builds UiDrawData per surface
	engine.Render(new FrameInputs { /* …existing… */ UiSurfaces = uiHost.Surfaces });
};
```

Touch arrives via `QueuePointer` with `PointerDeviceType.Touch` + distinct `PointerId`s. Pointer-moves coalesce
to the latest per `PointerId` inside `Tick` (WinUI intermediate points deferred); button/key edges are not coalesced.

### D5.3 Routed events (NAOT-clean)

```csharp
public enum RoutingStrategy { Tunnel, Bubble, Direct }
public sealed class RoutedEvent { public ushort Id { get; } public string Name { get; } public RoutingStrategy Routing { get; } }
```

Registered in static constructors (deterministic, AOT-safe, no assembly scanning). Paired tunnel/bubble like
WinUI (`PreviewKeyDown`/`KeyDown`). Handlers live in a lazily-allocated per-element store keyed by `RoutedEvent.Id`,
stored as **typed delegates** → invocation is a direct call (no `MethodInfo.Invoke`, no arg boxing). Args:

```
RoutedEventArgs { RoutedEvent OriginalEvent; object Source; object OriginalSource; bool Handled; }
 ├─ PointerRoutedEventArgs { PointerPoint Pointer; PointerDeviceType Device; KeyModifiers KeyModifiers; … }
 ├─ KeyRoutedEventArgs     { VirtualKey Key; bool KeyStatusRepeat; ModifierKeys Modifiers; }
 ├─ CharacterReceivedRoutedEventArgs { char Character; }
 └─ Tapped / DoubleTapped / RightTapped / Getting+LosingFocus …
```

Routing builds the hit element's ancestor chain (pooled `UIElement[]`), then tunnel root→target (Preview
handlers) then bubble target→root; `Handled` short-circuits both unless a node registered `handledEventsToo`.
Args objects are **pooled per-frame** (honor "0 GC in render loop"). Composite `Click`/`Tapped` are synthesized
from the pressed/released pair on the same captured element, then routed as their own bubble events. Generated
wiring (D1.7) emits `el.AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler(this.OnClick))` — typed, no reflection.

### D5.4 Hit-testing (2D + 3D)

**2D**, reverse z-order, after layout, respecting `IsHitTestVisible`/`Visibility`/clip/`RenderTransform`:

```csharp
internal static UIElement? HitTest(UIElement el, Vector2 point)   // point in el-local coords
{
	if (!el.IsHitTestVisible || el.Visibility != Visibility.Visible) return null;
	var local = el.RenderTransformInverse(point);
	if (el.ClipToBounds && !el.ClipRect.Contains(local)) return null;
	for (int i = el.ChildCount - 1; i >= 0; i--)                  // top-most first
	{
		var c = el.GetChild(i);
		var hit = HitTest(c, local - c.ArrangedOffset);
		if (hit is not null) return hit;
	}
	return el.RenderedBounds.Contains(local) && el.Background is not null ? el : null;
}
```

**3D in-world**: unproject the cursor with the **neutral** `Camera.ViewProjection` (never the backend Y-flipped
clip — that fix-up is backend-only), intersect each `UiWorldQuad`, map UV → UI-local, then run the same 2D walk:

```csharp
internal static bool TryHitWorldQuad(in Ray ray, in UiWorldQuad q, out Vector2 uiLocal, out float t)
{
	var inv = Matrix4x4.Invert(q.WorldTransform, out var m) ? m : Matrix4x4.Identity;
	var ol  = Vector3.Transform(ray.Origin, inv);                // into quad space (quad = XY plane, z=0)
	var dl  = Vector3.TransformNormal(ray.Direction, inv);
	if (MathF.Abs(dl.Z) < 1e-6f) { uiLocal = default; t = 0; return false; }
	t = -ol.Z / dl.Z;
	if (t <= 0) { uiLocal = default; return false; }
	var p = ol + dl * t;
	var u = p.X / q.SizeMeters.X + 0.5f; var v = 0.5f - p.Y / q.SizeMeters.Y;  // +Y up → v down
	if (u is < 0 or > 1 || v is < 0 or > 1) { uiLocal = default; return false; }
	uiLocal = new Vector2(u * q.Root.ActualWidth, v * q.Root.ActualHeight);
	return true;
}
```

**CPU-side, no GPU readback** — no `SamplePick` latency. Pointer capture routes all events for a `PointerId` to
the captured chain regardless of hit (drag/slider/press-then-leave). Enter/Exit diff prior-vs-current "over"
chains per `PointerId` (drives `:PointerOver` visual states). Surface priority: captured pointer > topmost screen
layer (popups/menus over HUD) > nearest in-world quad by ray `t`. Keyboard/gamepad go to the global focused element.

### D5.5 Focus & XYFocus (spatial nav)

- **Model:** focus scopes + one global focused element; `FocusManager` holds `FocusedElement` + `FocusState`
  (Pointer | Keyboard | Programmatic). `SetFocus` raises `GettingFocus`/`LosingFocus` (tunnel) then
  `GotFocus`/`LostFocus` (bubble). Focusable ⇔ `IsTabStop && IsEnabled && Visible` and focusable-by-type.
- **Tab / Shift-Tab:** order = `(TabIndex asc, document order)`; wrap at scope boundary unless
  `TabFocusNavigation = Once/Cycle/Contained`. Computed per layout pass, cached.
- **XYFocus (gamepad/D-pad/arrows):** nearest-in-direction heuristic — explicit `XYFocus*` overrides win first;
  otherwise score candidates in the requested half-plane by primary-axis distance + weighted secondary offset,
  strongly preferring projection-overlap. WinUI's exact constants aren't public, so weights are exposed as
  `XYFocusOptions` + a candidate-score **debug overlay** (the repo's "every feature ships with its debug view").
- **Gamepad mapping:** A = invoke focused element; B = `BackRequested` (close flyout/popup, else app-handled);
  D-pad/left-stick = `FocusNavigationDirection` (stick debounced to discrete steps with a repeat-delay timer).
- **Across surfaces:** v1 keeps focus *contained per active surface*; switching surfaces is explicit (shoulder
  button / tap). Cross-surface hop + "which surface owns the gamepad" deferred (open question).

### D5.6 Text input & IME

WinUI two-stream model: `KeyDown`/`KeyUp` (`KeyRoutedEventArgs`) for navigation/editing keys; `CharacterReceived`
(`CharacterReceivedRoutedEventArgs`) for produced text (from Silk `KeyChar`), routed to the focused element.
`TextBox` keeps `CaretIndex`/`SelectionStart`/`SelectionLength`; pointer sets caret via text-layout hit-test;
caret blink is an animation. **IME deferred** — v1 accepts committed chars (works for Latin + OS-committed input);
define an `ITextInputProvider` host seam now (Win32 TSF / X11-IBus / macOS later) so full IME lands without
reworking `TextBox`. v1 ships a no-IME provider.

### D5.7 Animation / storyboards / VisualStateManager

```
Timeline (Duration, BeginTime, RepeatBehavior, AutoReverse, FillBehavior)
 ├─ Storyboard : Timeline { Children }   // Storyboard.TargetName / TargetProperty
 ├─ DoubleAnimation { From?, To?, By?, EasingFunction }
 ├─ ColorAnimation / PointAnimation
 └─ DoubleAnimationUsingKeyFrames { KeyFrames: (KeyTime, Value, IEasingFunction) }
EasingFunction : CubicEase, QuadraticEase, BackEase, … + EasingMode (In/Out/InOut)
```

`AnimationClock` (persistent in `UiHost`) advances each active storyboard by `Tick`'s `deltaSeconds` (sourced from
`FrameInputs.DeltaSeconds` → `ctx.DeltaTime`): compute normalized progress (BeginTime/RepeatBehavior/AutoReverse),
ease, interpolate, push into the target DP's **animation rung** (`SetAnimatedValue`, D1.2). `FillBehavior.Stop` →
`ClearAnimatedValue` reverts to Local. `VisualStateManager.GoToState(control, "PointerOver", useTransitions)`
stops the current group's storyboard and starts the target's; `Button` calls it from `OnPointerEntered`/
`OnPointerPressed`/exit. Generated wiring resolves `TargetProperty="Opacity"` to a typed `ApplyValue` closure —
no runtime reflection:

```csharp
// GENERATED from <Storyboard><DoubleAnimation Storyboard.TargetName="Glow"
//                  Storyboard.TargetProperty="Opacity" To="1" Duration="0:0:0.2"/></Storyboard>
static Storyboard BuildHoverGlow(FrameworkElement glow) => new Storyboard
{
	Children = { new DoubleAnimation {
		Target = glow,
		ApplyValue = static (t, v) => t.SetAnimatedValue(UIElement.OpacityProperty, v),  // typed, no reflection
		To = 1.0, Duration = TimeSpan.FromMilliseconds(200),
		EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } } }
};
```

> **Hard cross-cutting dependency:** storyboards are *incorrect* unless the DP precedence ladder has the animation
> rung above Local (D1.2). It must exist from MU0, even though storyboards arrive at MU4.

---

## D6. Backend parity (Vulkan / DX12)

Symmetric, the shape ImGui already proves on both backends:

| Piece | Vulkan | DX12 |
|---|---|---|
| Backend renderer | `Loom.Vulkan/UiRenderer.cs` (twin of `ImGuiRenderer.cs`) | `Loom.DX12/DX12UiRenderer.cs` (twin of `DX12ImGuiRenderer.cs`) |
| Pipeline / PSO | alpha-over, dynamic viewport/scissor, dynamic rendering | alpha-over PSO, RTV bound by executor, negative-height viewport |
| Atlas upload | staging → device-local image, combined-image-sampler | upload → default-heap texture, SRV in the frame descriptor ring |
| Per-frame buffers | host-visible, grown per slot | UPLOAD-heap, grown per slot |
| Shaders | embedded `ui.spv` (+ `.json`) | embedded `ui.vs.dxil` / `ui.ps.dxil` — both from one `ui.slang` |
| Wire-in | `EnableUI()` resolves `ui.spv`, `SetUi(renderer)` | `EnableUI()` resolves the DXIL stages, sets the renderer |
| Neutral verb | `RenderContext.DrawUI` forwards `_cmd`/`_frameSlot`/`_fbExtent` | `DX12RenderContext.DrawUI` forwards the command list |

Core/Graph gain only the neutral `UiDrawData`/`UiSurfaceId` types + `IRenderContext.DrawUI` + `IEngine.EnableUI`
+ `FrameInputs.UiSurfaces`. Parity is verified by the established `LOOM_CAPTURE` + frozen-time cross-backend pixel
diff — the surface is deterministic given identical `UiDrawData`.

---

## D7. Open cross-cutting decisions (resolve as `Decision` cards)

1. **XAML subset grammar (D1.8)** — ratify and publish the in/out lists *before MU2* (the first binding milestone).
2. **HarfBuzzSharp at runtime vs bake-time shaping (D3.5)** — default to runtime HarfBuzz for v1; revisit if a
   fully-managed/no-native-in-loop constraint hardens (then bake pre-shaped runs for the fixed string table).
3. **In-world occlusion (D5.4)** — v1 accepts "ghostly" UI behind geometry; opt-in `SamplePick` depth occlusion
   (1–2 frame latency) is the documented escape hatch.
4. **Cross-surface focus & gamepad ownership (D5.5)** — v1 contains focus per active surface; design the hop later.
5. **IME (D5.6)** — committed-chars-only in v1 behind `ITextInputProvider`; full per-OS IME is post-v1.
6. **HDR "above paper-white" UI brushes (D4.4)** — opt-in glow under HDR; default is paper-white-clamped.

---

## D8. Testability (graphics-framework-free)

**Thesis.** The same layering that makes Loom's render graph testable applies to the UI engine *by
construction*. The Describe/Execute layers are fully unit-tested with no GPU — build an immutable graph, derive
a plan, assert on it (`RenderGraph.Define(…) → GraphSchedule/BarrierPlan/AliasPlan/LifetimePlan.Derive → assert`,
with a `GraphValidationException` for malformed input and a shared §6 reference-frame helper; see
[Loom.Tests](Loom.Tests/)). `Loom.UI` is shaped identically: it references **Core + Assets only**, takes **neutral
input** (events) and produces **neutral output** (`UiDrawData`). So everything *except* the backend
`UiRenderer`/`DX12UiRenderer` and the literal pixels is exercisable by fast, deterministic CPU tests — no window,
no engine, no Vulkan/DX12. The **headless-testable invariant** (Architecture §) is what guarantees this; protect it.

### D8.1 Inspection helpers as first-class API

Mirror the graph layer's `Hash()` / `Dump()` / `ToDot()` (used pervasively in the existing tests) with UI
equivalents, so tests assert on a stable text form rather than fiddly object graphs — and so each ships as the
**debug overlay** the design already calls for (the XYFocus score overlay, etc.):

- `UiDrawData.Dump()` — a deterministic text snapshot of the vertex/command stream (positions, colors, clip
  rects, `TextureId`, kind, `Revision`). This is the **"golden draw-list"**: the UI analog of a golden image, but
  CPU-only — deterministic, fast, diffable, and cross-platform. It is the primary way the whole 2D pipeline up to
  GPU upload is tested.
- `UiRoot.DumpLayout()` — the visual tree annotated with `DesiredSize` / `LayoutRect` after a layout pass.
- A `UiHost` state dump (focused element, capture, over-chains, active storyboards) for input/focus tests.

Malformed trees/bindings raise a **`UiValidationException`** (the analog of `GraphValidationException`); tests
cover happy-path *and* error-path, exactly as the graph tests do (e.g. "reading a transient before it is written
throws").

### D8.2 What is headless-testable (and how)

| Subsystem | Headless? | Assert on | Pattern |
|---|---|---|---|
| DP model + precedence ladder | ✅ | resolved value across rungs (animation > local > style > inherited > default); change thunk fires only on *resolved* change; `InvalidationFlags` propagate up the tree | set rungs → `GetValue` + dirty flags |
| Layout (measure/arrange) | ✅ | `DesiredSize`/`LayoutRect`: Grid star/auto/pixel, StackPanel, Canvas, Border, Margin/Min/Max/alignment | build tree → `LayoutPass(avail)` → assert (mirrors derivation tests) |
| Draw-list build | ✅ (neutral output) | `UiVertex` stream; `UiDrawCommand` batching (new cmd only on clip/texture/kind change); transform/clip/opacity fold; `TextureId = 0` white texel; `Revision`/no-op | render tree → `UiDrawData` → assert / `Dump()` snapshot |
| Routed events | ✅ | tunnel→bubble order, `Handled` gating, `handledEventsToo`, pooled route chain | dispatch synthetic event → assert handler order |
| Hit-test 2D | ✅ | element hit under a point; respects `IsHitTestVisible`/`Visibility`/clip/`RenderTransform` | build tree → `HitTest(point)` → assert |
| Hit-test 3D ray | ✅ (pure geometry, neutral `Camera`) | `TryHitWorldQuad` ray→quad→UV→UI-local mapping | feed `Ray` + `UiWorldQuad` + `Camera.ViewProjection` → assert UV/local |
| Focus + Tab | ✅ | order by `(TabIndex, doc order)`; wrap/contain at scope boundary | request Next/Prev → assert focused |
| XYFocus | ✅ | direction candidate selection; explicit `XYFocus*` overrides win (this is also how the private WinUI weights get tuned) | place rects → request direction → assert winner |
| Animation/storyboards | ✅ | eased/interpolated value over ticked time; push to animation rung; `FillBehavior` revert; `VSM.GoToState` | tick clock w/ synthetic delta → assert resolved DP |
| Text layout | ✅ w/ committed font fixture | measured size, glyph positions, line breaks, `screenPxRange` math, fallback→tofu | load tiny baked fontset fixture → layout string → assert runs/metrics |
| `UiHost.Tick` end-to-end | ✅ | focus moved / handler fired / capture set / `Render()` output | queue neutral events → `Tick` headless → assert |
| Source generator | ✅ (Roslyn, build-time) | emitted C# for representative `.xaml` (x:Bind page, styled/templated control, storyboard); diagnostics carry `file:line` | Roslyn snapshot/verify tests |
| NativeAOT cleanliness | ✅ (CI gate) | `PublishAot` smoke-publish; trim/AOT warnings as errors | CI job (also an MU0 deliverable) |
| Backend `UiRenderer` + pixels | ❌ GPU | rendered pixels match across backends | `LOOM_CAPTURE` frozen-time cross-backend pixel diff (the established loop) |

### D8.3 Test infrastructure

- **New `Loom.UI.Tests`** project (`net10.0`, **TUnit + Shouldly**) referencing `Loom.UI` (+ `Loom.Core`,
  `Loom.Assets`) and **no backend** — the same "neutral layers only, never Vulkan/DX12" rule that
  [Loom.Tests](Loom.Tests/Loom.Tests.csproj) follows. New UI-track tests are authored here directly in TUnit
  (`[Test]` / `[Arguments]` / data sources) with Shouldly assertions (`value.ShouldBe(…)`). *(The existing
  `Loom.Tests` is xUnit today and is being migrated to TUnit separately; that migration is out of scope here.)*
- **Source-gen snapshot tests** live in `Loom.UI.Tests` via the `Microsoft.CodeAnalysis.*` testing harness; split
  into a sibling `Loom.UI.SourceGen.Tests` *only* if the Roslyn-harness dependencies prove awkward to host beside
  the runtime tests (a fallback, not the default).
- **Committed fixtures (hermetic — no bake-tool run at test time):** a tiny MSDF fontset fixture (a handful of
  ASCII glyphs: atlas PNG + metrics JSON [+ subset `.ttf` for HarfBuzz]) for text-layout tests, plus a few small
  reusable reference UI trees — the UI analog of the §6 reference frame reused across the graph tests.

Net effect: the "Headless?" column is the acceptance contract. Every ✅ row must be reachable in `Loom.UI.Tests`
with no backend referenced; only the final ❌ row depends on the GPU and rides the existing capture loop.

