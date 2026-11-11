# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What Loom is

Loom is a declarative, render-graph-driven rendering engine for .NET 10. You declare *what* each pass reads and writes; the engine derives ordering, barriers, image-layout transitions, transient memory aliasing, and CPU↔GPU sync. The procedural, struct-heavy graphics-API code is quarantined in a backend layer and never leaks into the authoring API.

**[`Planning.md`](Planning.md) is the design bible — read it before any non-trivial work:** the core idea, the architecture, the Pillars of Design, the backend-portability posture, and the remaining roadmap. **[`README.md`](README.md)** covers the implemented features and how to use the API. This file (`CLAUDE.md`) is the operational layer: how the code is written, where the project is now, and the design pillars that must survive a context reset.

## Current state

Loom runs a full reference frame on **two backends at pixel parity — Vulkan and DirectX 12** — with Metal and WebGPU under investigation as further backends. The implemented-feature list and usage examples live in `README.md`; the design and remaining roadmap live in `Planning.md`.

Projects (dependency direction `Sandbox → {backends} → Graph → Core`):
- **`Loom.Core`** — the Describe layer (library): `GraphBuilder`/`PassBuilder`, typed handles, `Domain`, technique & vertex-layout builders, `Material`, the neutral draw-list types.
- **`Loom.Graph`** — the Execute layer: topological scheduling and derived barrier/layout/aliasing/lifetime plans, all backend-neutral.
- **`Loom.Backend.Vulkan` / `Loom.Backend.DirectX12`** — the backends (the marshaling seam).
- **`Loom.Assets`** — neutral image (StbImageSharp) + glTF (SharpGLTF) loaders, beside Core.
- **`Loom.Concepts`** — app-side helpers (`Camera`, `Frustum`, `Scene`); **not** part of Core/Graph.
- **`Loom.Gallery`** — the demo gallery as a library (all the `IDemo`s + their shaders/assets/UI views + the shared `DemoScene`/`DemoShell`/`DemoGraph`), referenced by both the sandbox exe and the tests so they run the same demos. App-tier: references no backend.
- **`SUIsei`** (the `external/SUIsei` submodule) — a standalone, GPU-free XAML UI runtime + `SUIsei.SourceGen` + `SUIsei.Tests`; integrated through `Loom.UI.SUIsei`. `Loom.UI.ImGui` is the debug-HUD bridge. Both reach the GPU only through the neutral draw-list verb, so neither backend references a UI library.
- **`Loom.Tests`** — TUnit + Shouldly; describe/execute-layer + mip tests + in-process GPU-gated golden/parity tests over `Loom.Gallery`.
- **`Loom.Sandbox`** — the gallery's desktop `Exe`; a thin `LoomApplication` (`SandboxApp`) hosting `Loom.Gallery` under `Loom.Hosting.Desktop` (see below).
- **`Loom.Hosting.Android`** + **`samples/HelloUI`** — the Android host (`net11.0-android`, CoreCLR: a `LoomActivity` the app subclasses, Vulkan on the `SurfaceView`'s `ANativeWindow`, Choreographer-paced frames on the UI thread, touch → pointer + `Touch` events, `IReattachableSurface` across the app leaving the screen) and a SUIsei sample building both a desktop exe and an APK. Both live in **`Loom.Android.slnx`**, not `Loom.slnx`, so the desktop build never needs the `android` workload; use that solution for Roslyn calls on them. The Loom SDK treats an `android` target framework as Vulkan-only with embedded shaders. No Android device or emulator is attached by default: build the APK here, deploy with `-t:Run` when one is (see `samples/HelloUI/README.md`).

All target `net10.0`; the solution is `Loom.slnx`. Key operational facts: the host hands the backend the window's surface opaquely (Vulkan `IVkSurface`, DX12 `HWND`); 2 frames in flight; shaders are Slang compiled offline to SPIR-V + signed DXIL + WGSL with reflection-derived descriptor layouts; backends self-register into `Backends.Available` guarded by an `IsSupported()` probe.

Dev-environment notes:
- **Don't assume the host architecture or GPU** — this repo is developed across more than one machine (both Windows-on-ARM and x64 boxes have been used). Detect it at the start of a session rather than hard-coding: `dotnet --info` (or `$env:PROCESSOR_ARCHITECTURE`) for the CPU arch, and `Get-CimInstance Win32_VideoController | Select-Object Name` for the GPU. Keep engine code arch/GPU-agnostic (or behind a capability check).
- **Vulkan SDK 1.4.350.0** is installed (validation layers available). Note: `winget` can't install it from a non-interactive shell (the UAC elevation gets cancelled) — install from an interactive elevated terminal.
- A benign `[vk:WARN]` about duplicate `VK_LAYER_MSFT_driver_sorting` manifests (Microsoft `D3DMappingLayers`) prints at startup; it's a loader/environment quirk, not a Loom issue.
- `Loom.Sandbox` is a `LoomApplication` under `Loom.Hosting.Desktop` — **no `LOOM_*` environment variables**, and **no headless/capture mode**: a run always opens a window and blocks until closed. The host owns backend/presentation flags (`--backend <vulkan|directx12>` (`dx12` alias), `--vsync`/`--no-vsync`, `--max-fps`, `--hdr`, `--watch-shaders`, `--software-adapter`, `-h`); the app owns `--demo <name|index|substring>`, `--dump-graph <prefix>`, `--inspect`, `--frustum`, `--no-hud`. (The `LOOM_VULKAN`/`LOOM_DIRECTX12`/… symbols are *compile-time* build constants, not runtime vars.)
  - ⚠️ **Every sandbox run is interactive — it opens a window and blocks until that window is closed.** There is no `--capture`/`--smoke-frames` <!-- LOOM-RETIRED-OK -->; a bare `dotnet run --project Loom.Sandbox` never returns in an agent context. For **headless verification** use the in-process tests (`Loom.Tests`: `GalleryGoldenTests`/`GalleryParityTests` render the gallery offscreen). To smoke the sandbox itself, launch the built exe and kill it after a few seconds (see the interactive smoke pattern), watching for the `Loom - demo gallery … on <device> (<backend>)` startup line.
## Loom.Sandbox

`Loom.Sandbox` is the runnable demo gallery and the shared host for both backends — `--backend` swaps only the window surface + engine settings (owned by `Loom.Hosting.Desktop`); the gallery (`Loom.Gallery`) and the per-frame loop are shared, so the two backends can't drift out of sync. When a demo renders on one backend but not the other, suspect the shared per-frame inputs or a genuine backend/shader difference.

`Program.cs` is one line (`LoomApplication.Run(new SandboxApp(), args)`); `SandboxApp` maps the gallery onto `Configure`/`LoadContent`/`Update`/`Draw`. Flags (`-- --help` shows the host's; the app's are `--demo`/`--dump-graph`/`--inspect`/`--frustum`/`--no-hud`):
- `--backend <vulkan|directx12>` (`dx12` is an accepted alias) — pick the backend (host option).
- `--demo <name|index|substring>` — pick the demo (an exact case-insensitive name, an index, or a unique case-insensitive substring; an ambiguous or unmatched value is reported and the sandbox exits).

⚠️ **Every run is interactive** — it opens a window and blocks until closed. There is no capture/smoke flag; deterministic rendering verification lives in `Loom.Tests` (in-process, offscreen).

## Dev environment

- The repo targets **both x64 and Windows-on-ARM** (the maintainer's box is a Snapdragon X / Adreno X1-85). **Don't assume a CPU architecture** — detect it (`RuntimeInformation.ProcessArchitecture`) before relying on arch-specific behaviour, and don't assume the machine you're on is either one.
- **Vulkan SDK 1.4.350.0** provides the validation layers. `winget` can't install it from a non-interactive shell (the UAC elevation gets cancelled) — install from an interactive elevated terminal.
- On the ARM box a benign `[vk:WARN]` about a duplicate `VK_LAYER_MSFT_driver_sorting` manifest (Microsoft `D3DMappingLayers`) prints at startup — a loader/environment quirk, not a Loom issue.

## Progress tracking

Progress is tracked as **GitHub issues** on `AtomicBlom/Loom`. (The Projects v2 board was used for milestone-era planning and is being retired; once `Planning.md` settles, its roadmap is distilled into issues.) `Planning.md` is the source of truth for the design + remaining roadmap.

## Commands

    dotnet build Loom.slnx                            # build the whole solution
    dotnet run   --project Loom.Sandbox               # run the sandbox demo gallery (interactive)
    dotnet build Loom.Core                            # build a single project
    dotnet run   --project Loom.Tests                 # run the unit tests (TUnit on Microsoft.Testing.Platform)
    dotnet format Loom.slnx                           # apply the .editorconfig conventions

**Packages:** versions are centralized via **Central Package Management** — `Directory.Packages.props` at the repo root holds every `<PackageVersion>`, and the project `.csproj` files carry version-less `<PackageReference>`s. The vendored `external/VMASharp` submodule opts out (its own `external/Directory.Packages.props` sets `ManagePackageVersionsCentrally=false`) and keeps its inline versions — don't add its packages to the root file.

## Tests

Tests live in `Loom.Tests` (and `external/SUIsei/SUIsei.Tests`) and cover the describe/execute layers — scheduling, barrier derivation, aliasing, lifetimes, graph hashing, mip generation — on **TUnit** (Microsoft.Testing.Platform) with **Shouldly** assertions.

- Run them with **`dotnet run --project Loom.Tests`** (the MTP test app). Run a single class/test with `dotnet run --project Loom.Tests -- --treenode-filter "/*/*/<ClassName>/<MethodName>"`.
- **Also run them NativeAOT-published** to guard AOT/trim-safety (the engine and SUIsei must stay NativeAOT-clean): `dotnet publish Loom.Tests -c Release -r <rid> -p:PublishAot=true -p:RestoreUseStaticGraphEvaluation=true` and run the emitted native test executable. The static-graph flag is required because `Loom.Tests` transitively references the SUIsei XAML source generator (a netstandard2.0 analyzer, via `Loom.Gallery`) and a plain restore propagates `PublishAot` into it (NETSDK1207) — the proper fix is `UndefineProperties="PublishAot;RuntimeIdentifier;SelfContained"` on the analyzer `ProjectReference` in `SUIsei.targets` (a submodule change). Treat new trim/AOT warnings *from Loom's own assemblies* as failures; `SharpGLTF` (System.Text.Json reflection) and `Shouldly` emit pre-existing third-party IL2026/IL3053 warnings. Under the repo-wide `TreatWarningsAsErrors` those become ILC errors, so the test publish also needs `-p:TreatWarningsAsErrors=false` (then check the ILC warnings by assembly); and the native link finds MSVC through `vswhere.exe`, so put `C:\Program Files (x86)\Microsoft Visual Studio\Installer` on `PATH` if a shell lacks it. Run the published binary from its own folder for everything except `RetiredSpellingTests`, which finds the repo four directories above the binary and so only means something in an in-tree `bin/<config>/<tfm>` run.
- Golden-image + cross-backend parity are in-process GPU-gated tests over `Loom.Gallery`: `GalleryGoldenTests` renders the tracked demos offscreen and diffs them against committed baselines under `Loom.Tests/Goldens/` (`LOOM_GOLDEN_UPDATE` regenerates them; `tools/golden.ps1 -Update` wraps that), and `GalleryParityTests` diffs Vulkan-vs-DirectX12 renders directly. Both SKIP when the backend they need is absent, so CI (no GPU) skips them. The UI demos are in both: `GalleryCapture` shows a demo's own page (`IDemo.OverlayContent`) in a chrome-hidden shell and replays its scripted click (`IDemo.CaptureClick`), exactly as the browser's capture mode does, and `UIDemoCaptureTests` fails if a UI demo's UI stops reaching the frame (two blank-UI frames would otherwise pass both). The baselines are GPU-specific: regenerating them updates every tracked demo, so to add one, keep only the new PNG and restore the rest.
- **Targeted controlled tests** complement the gallery goldens/parity (broad but low-resolution — a failure doesn't say *which* stage diverged): each renders a minimal single-feature scene offscreen to one target, confounds stripped, and asserts one thing. Three flavours — parity **with a control** (`TargetedParityTests`; e.g. the normal-map G-buffer's encoded normal diffed beside the *same shader's* albedo, pinning a divergence to the TBN math not the sampler), **self-consistency** (`RenderDeterminismTests`; a repeat render is bit-identical — what the goldens rest on), and **property/invariant** (`RenderPropertyTests`; group-opacity overlap == non-overlap, tone-map monotonic). Reach for one when a feature is subtle, backend-divergence-prone, or has bitten — not reflexively. Enablers: the offscreen path (`GalleryCapture`), a fullscreen `probe` shader (a pushed RGBA×UV gradient, HDR-capable) for controlled colour/HDR inputs, and `InWorldPanel` for rendering a UI tree to a readable target. **Gotchas:** reason about a cross-backend diff with the parity metric (`ImageComparison.Compare` — a pixel fails if *any* channel exceeds tolerance); `Scripts/Verification/diff.ps1` *averages* the four channels and can hide a single-channel divergence. And guard these against a vacuous pass — two flat/black frames also compare as equal, so assert the render actually has content.

## Coding conventions

Enforced by `.editorconfig` at the repo root; the essentials:
- **File-scoped namespaces** (`namespace Loom.Core;`), never block-scoped.
- **Tabs, not spaces** for `.cs`, `.csproj`, `.slnx`, `.json`.
- **Braces where appropriate** — required around multi-line blocks; a single-line `if (cond) return;` may omit them.
- **Single class per file** — inner classes permissible where appropriate.
- **Modern .NET / C#:**
  - `var` when the type is apparent; target-typed `new`; collection expressions.
  - pattern matching / switch expressions; primary constructors; expression-bodied members.
  - `using` declarations; null-propagation and coalescing.
  - Nullable reference types and implicit usings are on.
  - `field` in properties instead of a backing `_field`.
  - `with` to copy records with changes.
  - **C# 14 extension members** — `extension` blocks (extension properties, indexers, and static members), not just extension methods.
- Allman braces; `System.*` usings sorted first; usings outside the namespace.
- **UI-track naming.** The two-letter acronym `UI` is always uppercase in identifiers — matching WinUI's own `UIElement` and the .NET two-letter-acronym guideline: `UIElement`, `UIRoot`, `UIVertex`, `UIDrawData`, `UISurfaceId`, `UIPrimKind`, `IUICompositor`, `UIRenderer`. (In camelCase a leading `ui` stays lowercase, e.g. the `uiSurfaces` local.) `Loom.sln.DotSettings` registers `UI` as a known abbreviation so Rider/ReSharper accepts it — keep that file.
- **Comments are self-contained.** A comment describes what the code does or *why* — never the context in which it was written. Do **not** reference a milestone, phase, or planning-doc section (`M13`, `MU1`, `M7-B4`, `§D2.1`, `Planning.md §8`), and do **not** narrate project history or schedule (`for now`, `previously`, `no longer`, `lands in MU2`, `arrives later`) — that belongs in commit messages, not the source, because it goes stale and forces the reader to look something up. **Do** keep XML docs on public APIs, and explanations of a non-obvious algorithm, gotcha, or "why this and not that". When a real explanation carries a milestone tag, strip the tag and keep the explanation.

**Don't hand-fix formatting.** Run `dotnet format Loom.slnx` rather than manually matching tabs/braces/usings, and never loop on your own formatting mistakes — a `Stop` hook in `.claude/settings.json` auto-runs `dotnet format` on changed C# at the end of each turn, so let the tool own formatting.

## Goals & constraints

- **Cross-platform from the start.** Windows, Linux, and macOS. Silk.NET, Vulkan, and the windowing/input stack are cross-platform — keep OS-specific APIs out of the engine (or behind a capability check). Don't bake in Win32-only assumptions.
- **Backend-portable by design.** Loom runs on **Vulkan and DirectX 12 in parallel at pixel parity**, with **Metal and WebGPU under investigation** as further backends. This makes backend isolation load-bearing in practice: every new GPU feature must land in (or be `<Use…>`-gated for) every backend, and **no graphics-API type may enter the describe/execute layers**.

## Architecture in one screen

Three layers, mapped to projects and kept strictly separate — this separation is what keeps the design manageable, so protect it:

- **Describe** (`Loom.Core`) — immutable, hashable, fluent builders. The only layer authored against directly; allocates freely.
- **Execute** (`Loom.Graph`) — per frame: topological sort from the read/write edges, *derive* the barrier/layout-transition and aliasing decisions, drive command recording. Produces **backend-neutral** decisions (no graphics-API types) so the same plan targets any backend.
- **Bake + record** (the backends) — marshal descriptions transiently to native structs, create pipelines/descriptor layouts (cached by hash), and translate Execute's neutral decisions into real GPU work. The "marshaling seam" — the only place procedural graphics-API code lives.

**Dependency direction: `Sandbox → {backends} → Graph → Core`.** `Loom.Core` and `Loom.Graph` carry **no** graphics-API / Silk.NET reference. Introducing a graphics-API type into Core or Graph is the cardinal architectural violation — it breaks backend portability.

## The Pillars of Design

Load-bearing; violating one silently breaks the design (the full statement is in `Planning.md` §3):
- **Derived, not declared.** Users state reads/writes only. Any API that asks the user to specify *order* is a smell.
- **`.Writes` vs `.Blends`.** `.Writes` = produce (engine may clear). `.Blends` = accumulate onto existing contents (`LoadOp.Load` + blend; the pass is both last-reader and next-writer). Barrier derivation needs the distinction.
- **Backend isolation / portability.** All procedural, struct-heavy, API-specific code stays in the backends. Core/Graph speak in abstract resources and intents so every backend stays swappable.
- **Pure per-frame closures.** The graph is built once and cached; each `.Draw`/`.Dispatch` body runs every frame. So a closure may capture **stable objects** — engine handles and app-side stable producers (a painter, a scene) — and read their **live** members at execute time; it must **not** capture a per-frame **value** (that snapshot freezes at build). Pull per-frame facts from `ctx` and per-frame data from neutral `Constants`/`Storage` buffers or from a stable producer passed as pass state. The `Draw<TState>`/`Dispatch<TState>` overloads let a body be written as a `static` lambda so accidental per-frame capture cannot compile.
- **No allocations on the hot path.** The steady per-frame render loop allocates no managed memory (especially in Release) — preallocated/pooled buffers and `ref struct`s, and the graph is built once and cached. Applies to SUIsei's per-frame tick/route/composite path too.
- **The engine names no game concepts.** No `Camera`/`Sun`/`Scene` in Core/Graph; per-frame view/light/time data rides neutral channels, and the app names its own views.
- **Ping-pong via handle swap.** Two physical resources + a `(read, write)` tuple swapped in a normal loop; the graph follows the handles (bloom, JFA).
- **Fail loud, never silently degrade.** Backend mapping switches throw on an unmapped value; over-promised APIs throw at derive time rather than doing the wrong thing.

## Agent behaviour

Image captures take up a LOT of context. When verifying whether an image is **correct**, use a subagent and tell it what you expect the image to contain in as much detail as possible; the subagent returns whether it met your expectations.

For **objective, quantitative** checks that need no correctness judgment — "did anything render?", "are the two backends at pixel parity?" — prefer the checked-in `Scripts/Verification/` helpers over ad-hoc `System.Drawing` snippets; they keep image bytes out of context entirely:
- `pwsh Scripts/Verification/colorcount.ps1 -Path <png>` — distinct-RGB count (a black/empty frame is ~1 colour; a real scene is thousands).
- `pwsh Scripts/Verification/diff.ps1 -A <png> -B <png> -Out <heatmap.png>` — mean/max per-channel abs diff + the fraction of pixels over `-Threshold`, plus a heatmap. Cross-backend parity bar: 4/255 per channel, <0.5% of pixels.

Cross-backend parity of the gallery demos is enforced in-process by `GalleryParityTests` (no capture flags). These helpers are for ad-hoc inspection of any PNG you have — a test capture, a one-off `IEngine.RequestCapture` output, a bug repro. See `Scripts/Verification/README.md`.

## Roslyn MCP

A `roslyn-refactor` MCP server is configured in `.mcp.json` and **must be preferred over text-based tools for all C# work when available**. It is not installed by default — install it once with:

```
dotnet tool install -g RoslynMcp.Server
```

The command registered is `roslyn-mcp`. The server reads from disk on every call so the workspace is always current after a save — no reload needed between an edit and a query. The solution path for all calls is the repo-root-relative `Loom.slnx` (resolve it against the current working directory so calls hit whichever worktree you're in — never a hard-coded main-worktree path). **Exception — the browser half:** `Loom.Web` (the WASM host) is kept out of `Loom.slnx` so the wasm-tools workload stays off the desktop build; it lives in `Loom.Web.slnx` alongside the WebGPU backend and the engine layers it references. Use `Loom.Web.slnx` for Roslyn calls that touch `Loom.Web`. Two known limitations on the web solution: (1) design-time diagnostics report spurious `CS8795` "partial method must have an implementation" errors for the `[JSImport]` host partials, because the JSImport source generator only runs in the wasm build — verify the browser projects with `dotnet build`/`publish`, not Roslyn diagnostics;
**Never use `Grep` to search `.cs` files for symbols, usages, or callers** — text search misses renames, overloads, and implicit implementations. Use the semantic equivalents:

| Task | Tool |
|---|---|
| Find a type/member by name | `search_symbols` |
| Find all usages of a symbol | `find_references` |
| Find call sites of a method | `find_callers` |
| Find implementations of an interface/virtual | `find_implementations` |
| Navigate to a definition | `go_to_definition` |
| List all members of a file | `get_document_outline` |
| Get type/member metadata | `get_symbol_info` |
| Inspect inheritance hierarchy | `get_type_hierarchy` |

**All refactoring operations must go through Roslyn** when a matching tool exists — not manual text edits:

- **Rename** any symbol: `rename_symbol` (updates all references atomically)
- **Extract** method / variable / constant / interface / base class: `extract_method`, `extract_variable`, `extract_constant`, `extract_interface`, `extract_base_class`
- **Move** type to its own file or namespace: `move_type_to_file`, `move_type_to_namespace`
- **Change** a method's signature: `change_signature`
- **Convert** expression body ↔ block, `foreach` → LINQ, property kinds, sync → async, string concat → interpolation, `if`/`switch` → pattern matching: `convert_*`
- **Generate** constructor / `Equals`+`GetHashCode` / overrides / `ToString`: `generate_*`
- **Usings**: `add_missing_usings`, `remove_unused_usings`, `sort_usings`

**After editing any `.cs` file**, call `get_diagnostics` (with `severityFilter: "Error"`) before declaring the change done — the server picks up saved files immediately.

Make sure you tell this to ANY sub agents as well.
