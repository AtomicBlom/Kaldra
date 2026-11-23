# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What Kaldra is

Kaldra is a declarative, render-graph-driven rendering engine for .NET 11. You declare *what* each pass reads and writes; the engine derives ordering, barriers, image-layout transitions, transient memory aliasing, and CPU↔GPU sync. The procedural, struct-heavy graphics-API code is quarantined in a backend layer and never leaks into the authoring API.

**[`Planning.md`](Planning.md) is the design bible — read it before any non-trivial work:** the core idea, the architecture, the Pillars of Design, the backend-portability posture, and the remaining roadmap. **[`README.md`](README.md)** covers the implemented features and how to use the API. This file (`CLAUDE.md`) is the operational layer: how the code is written, where the project is now, and the design pillars that must survive a context reset.

## Current state

Kaldra runs a full reference frame on **two backends at pixel parity — Vulkan and DirectX 12** — with Metal and WebGPU under investigation as further backends. The implemented-feature list and usage examples live in `README.md`; the design and remaining roadmap live in `Planning.md`.

Projects (dependency direction `app → {backends} → Graph → Core`):
- **`BinaryVibrance.Kaldra.Core`** — the Describe layer (library): `GraphBuilder`/`PassBuilder`, typed handles, `Domain`, technique & vertex-layout builders, `Material`, the neutral draw-list types.
- **`BinaryVibrance.Kaldra.Graph`** — the Execute layer: topological scheduling and derived barrier/layout/aliasing/lifetime plans, all backend-neutral.
- **`BinaryVibrance.Kaldra.Backend.Vulkan` / `BinaryVibrance.Kaldra.Backend.DirectX12`** — the backends (the marshaling seam).
- **`BinaryVibrance.Kaldra.Assets`** — neutral image (StbImageSharp) + glTF (SharpGLTF) loaders, beside Core.
- **`BinaryVibrance.Kaldra.Concepts`** — app-side helpers (`Camera`, `Frustum`, `Scene`); **not** part of Core/Graph.
- **`BinaryVibrance.Vysara`** ([its own repository](https://github.com/AtomicBlom/Vysara), a checkout beside this one: `..\Vysara`, or wherever `VysaraSource` says, `build/Vysara.Source.props`) — a standalone, GPU-free XAML UI runtime and its XAML generator; integrated through `BinaryVibrance.Kaldra.UI.Vysara`. `BinaryVibrance.Kaldra.UI.ImGui` is the debug-HUD bridge. Both reach the GPU only through the neutral draw-list verb, so neither backend references a UI library.
- **`BinaryVibrance.Kaldra.Tests`** — TUnit + Shouldly; the engines: describe/execute-layer + mip tests, the backends, hosts, SDK and the Vysara adapter, with test shaders of its own (`BinaryVibrance.Kaldra.Tests/Shaders`).
- **`samples/HelloTriangle`**, **`samples/HelloUI`** — the smallest app, and a Vysara app on the desktop and Android.
- **`BinaryVibrance.Kaldra.Hosting.Android`** — the Android host (`net11.0-android`, CoreCLR: a `KaldraActivity` the app subclasses, Vulkan on the `SurfaceView`'s `ANativeWindow`, Choreographer-paced frames on the UI thread, `IReattachableSurface` across the app leaving the screen; input: touch → pointer + `Touch` events, keyboard keys + text, Back → Escape, the on-screen keyboard driven by `InputContext.RequestTextInput` with its height as `KaldraApplication.InputPaneHeight`, mouse/pen buttons, game controllers + rumble; `OnSuspending`/`OnResumed` on `OnPause`/`OnResume`; the cutout as `KaldraApplication.SafeAreaInsets`; `KaldraContent` items packed as APK assets and read with `KaldraApplication.OpenContent`); `samples/HelloUI` builds both a desktop exe and an APK on it. Both live in **`BinaryVibrance.Kaldra.Android.slnx`**, not `BinaryVibrance.Kaldra.slnx`, so the desktop build never needs the `android` workload; use that solution for Roslyn calls on them. The Kaldra SDK treats an `android` target framework as Vulkan-only with embedded shaders. No Android device or emulator is attached by default: build the APK here, deploy with `-t:Run` when one is (see `samples/HelloUI/README.md`); check the phone's screen with the `examine-image` skill (`phone`), drive it with `adb shell input`.

**What moved out** (each its own repository, built on a checkout of this one or its packages): the demo gallery, its sandboxes, the browser host `BinaryVibrance.Kaldra.Web` and the GPU golden-image and parity tests are [Kaldra.Gallery](https://github.com/AtomicBlom/Kaldra.Gallery); the audio and synthesis are [Sirrow](https://github.com/AtomicBlom/Sirrow); the music workbench is [SirrowStudio](https://github.com/AtomicBlom/SirrowStudio). The engine knows nothing of the audio: an app wires the two together. `InternalsVisibleTo BinaryVibrance.Kaldra.Gallery.Tests` stays on the backends, the desktop host and the Vysara adapter, for the gallery's tests.

All target `net11.0`; the solution is `BinaryVibrance.Kaldra.slnx`. Key operational facts: the host hands the backend the window's surface opaquely (Vulkan `IVkSurface`, DX12 `HWND`); 2 frames in flight; shaders are Slang compiled offline to SPIR-V + signed DXIL + WGSL with reflection-derived descriptor layouts; backends self-register into `Backends.Available` guarded by an `IsSupported()` probe.

Dev-environment notes:
- **Don't assume the host architecture or GPU** — this repo is developed across more than one machine (both Windows-on-ARM and x64 boxes have been used). Detect it at the start of a session rather than hard-coding: `dotnet --info` (or `$env:PROCESSOR_ARCHITECTURE`) for the CPU arch, and `Get-CimInstance Win32_VideoController | Select-Object Name` for the GPU. Keep engine code arch/GPU-agnostic (or behind a capability check).
- **Vulkan SDK 1.4.350.0** is installed (validation layers available). Note: `winget` can't install it from a non-interactive shell (the UAC elevation gets cancelled) — install from an interactive elevated terminal.
- A benign `[vk:WARN]` about duplicate `VK_LAYER_MSFT_driver_sorting` manifests (Microsoft `D3DMappingLayers`) prints at startup; it's a loader/environment quirk, not a Kaldra issue.
- An app is a `KaldraApplication` under `BinaryVibrance.Kaldra.Hosting.Desktop` — **no `KALDRA_*` environment variables**, and **no headless/capture mode**: a run always opens a window and blocks until closed. The host owns backend/presentation flags (`--backend <vulkan|directx12>` (`dx12` alias), `--vsync`/`--no-vsync`, `--max-fps`, `--hdr`, `--watch-shaders`, `--software-adapter`, `-h`). (The `KALDRA_VULKAN`/`KALDRA_DIRECTX12`/… symbols are *compile-time* build constants, not runtime vars.)
  - ⚠️ **Every run is interactive — it opens a window and blocks until that window is closed.** There is no `--capture`/`--smoke-frames` <!-- KALDRA-RETIRED-OK -->; a bare `dotnet run --project samples/HelloTriangle` never returns in an agent context. For **headless verification** use the in-process tests (`BinaryVibrance.Kaldra.Tests` renders offscreen). To smoke an app, launch the built exe and kill it after a few seconds.

## Dev environment

- The repo targets **both x64 and Windows-on-ARM** (the maintainer's box is a Snapdragon X / Adreno X1-85). **Don't assume a CPU architecture** — detect it (`RuntimeInformation.ProcessArchitecture`) before relying on arch-specific behaviour, and don't assume the machine you're on is either one.
- **Vulkan SDK 1.4.350.0** provides the validation layers. `winget` can't install it from a non-interactive shell (the UAC elevation gets cancelled) — install from an interactive elevated terminal.
- On the ARM box a benign `[vk:WARN]` about a duplicate `VK_LAYER_MSFT_driver_sorting` manifest (Microsoft `D3DMappingLayers`) prints at startup — a loader/environment quirk, not a Kaldra issue.

## Progress tracking

Progress is tracked as **GitHub issues** on `AtomicBlom/Kaldra`. (The Projects v2 board was used for milestone-era planning and is being retired; once `Planning.md` settles, its roadmap is distilled into issues.) `Planning.md` is the source of truth for the design + remaining roadmap.

## Commands

    dotnet build BinaryVibrance.Kaldra.slnx                            # build the whole solution
    dotnet run   --project samples/HelloTriangle      # the smallest app (interactive)
    dotnet build BinaryVibrance.Kaldra.Core                            # build a single project
    dotnet run   --project BinaryVibrance.Kaldra.Tests                 # the engine's unit tests (TUnit on Microsoft.Testing.Platform)
    dotnet format BinaryVibrance.Kaldra.slnx                           # apply the .editorconfig conventions

**Packages:** versions are centralized via **Central Package Management** — `Directory.Packages.props` at the repo root holds every `<PackageVersion>`, and the project `.csproj` files carry version-less `<PackageReference>`s. The vendored `external/VMASharp` submodule opts out (its own `external/Directory.Packages.props` sets `ManagePackageVersionsCentrally=false`) and keeps its inline versions — don't add its packages to the root file.

## Tests

Tests live in `BinaryVibrance.Kaldra.Tests` (Vysara's in its own repository) and cover the describe/execute layers — scheduling, barrier derivation, aliasing, lifetimes, graph hashing, mip generation — on **TUnit** (Microsoft.Testing.Platform) with **Shouldly** assertions.

- Run them with **`dotnet run --project BinaryVibrance.Kaldra.Tests`** (the MTP test app). Run a single class/test with `dotnet run --project BinaryVibrance.Kaldra.Tests -- --treenode-filter "/*/*/<ClassName>/<MethodName>"`.
- **Also run them NativeAOT-published** to guard AOT/trim-safety (the engine and Vysara must stay NativeAOT-clean): `dotnet publish BinaryVibrance.Kaldra.Tests -c Release -r <rid> -p:PublishAot=true -p:RestoreUseStaticGraphEvaluation=true` and run the emitted native test executable. The static-graph flag is required because `BinaryVibrance.Kaldra.Tests` transitively references the Vysara XAML source generator (a netstandard2.0 analyzer, via `BinaryVibrance.Kaldra.UI.Vysara`) and a plain restore propagates `PublishAot` into it (NETSDK1207) — the proper fix is `UndefineProperties="PublishAot;RuntimeIdentifier;SelfContained"` on the analyzer `ProjectReference` in `Vysara.targets` (a submodule change). Treat new trim/AOT warnings *from Kaldra's own assemblies* as failures; `SharpGLTF` (System.Text.Json reflection) and `Shouldly` emit pre-existing third-party IL2026/IL3053 warnings. Under the repo-wide `TreatWarningsAsErrors` those become ILC errors, so the test publish also needs `-p:TreatWarningsAsErrors=false` (then check the ILC warnings by assembly); and the native link finds MSVC through `vswhere.exe`, so put `C:\Program Files (x86)\Microsoft Visual Studio\Installer` on `PATH` if a shell lacks it. Run the published binary from its own folder for everything except `RetiredSpellingTests`, which finds the repo four directories above the binary and so only means something in an in-tree `bin/<config>/<tfm>` run.

## Coding conventions

Enforced by `.editorconfig` at the repo root; the essentials:
- **File-scoped namespaces** (`namespace BinaryVibrance.Kaldra.Core;`), never block-scoped.
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
- **UI-track naming.** The two-letter acronym `UI` is always uppercase in identifiers — matching WinUI's own `UIElement` and the .NET two-letter-acronym guideline: `UIElement`, `UIRoot`, `UIVertex`, `UIDrawData`, `UISurfaceId`, `UIPrimKind`, `IUICompositor`, `UIRenderer`. (In camelCase a leading `ui` stays lowercase, e.g. the `uiSurfaces` local.) `BinaryVibrance.Kaldra.sln.DotSettings` registers `UI` as a known abbreviation so Rider/ReSharper accepts it — keep that file.
- **Comments are self-contained.** A comment describes what the code does or *why* — never the context in which it was written. Do **not** reference a milestone, phase, or planning-doc section (`M13`, `MU1`, `M7-B4`, `§D2.1`, `Planning.md §8`), and do **not** narrate project history or schedule (`for now`, `previously`, `no longer`, `lands in MU2`, `arrives later`) — that belongs in commit messages, not the source, because it goes stale and forces the reader to look something up. **Do** keep XML docs on public APIs, and explanations of a non-obvious algorithm, gotcha, or "why this and not that". When a real explanation carries a milestone tag, strip the tag and keep the explanation.

**Don't hand-fix formatting.** Run `dotnet format BinaryVibrance.Kaldra.slnx` rather than manually matching tabs/braces/usings, and never loop on your own formatting mistakes — a `Stop` hook in `.claude/settings.json` auto-runs `dotnet format` on changed C# at the end of each turn, so let the tool own formatting.

## Goals & constraints

- **Cross-platform from the start.** Windows, Linux, and macOS. Silk.NET, Vulkan, and the windowing/input stack are cross-platform — keep OS-specific APIs out of the engine (or behind a capability check). Don't bake in Win32-only assumptions.
- **Backend-portable by design.** Kaldra runs on **Vulkan and DirectX 12 in parallel at pixel parity**, with **Metal and WebGPU under investigation** as further backends. This makes backend isolation load-bearing in practice: every new GPU feature must land in (or be `<Use…>`-gated for) every backend, and **no graphics-API type may enter the describe/execute layers**.

## Architecture in one screen

Three layers, mapped to projects and kept strictly separate — this separation is what keeps the design manageable, so protect it:

- **Describe** (`BinaryVibrance.Kaldra.Core`) — immutable, hashable, fluent builders. The only layer authored against directly; allocates freely.
- **Execute** (`BinaryVibrance.Kaldra.Graph`) — per frame: topological sort from the read/write edges, *derive* the barrier/layout-transition and aliasing decisions, drive command recording. Produces **backend-neutral** decisions (no graphics-API types) so the same plan targets any backend.
- **Bake + record** (the backends) — marshal descriptions transiently to native structs, create pipelines/descriptor layouts (cached by hash), and translate Execute's neutral decisions into real GPU work. The "marshaling seam" — the only place procedural graphics-API code lives.

**Dependency direction: `Sandbox → {backends} → Graph → Core`.** `BinaryVibrance.Kaldra.Core` and `BinaryVibrance.Kaldra.Graph` carry **no** graphics-API / Silk.NET reference. Introducing a graphics-API type into Core or Graph is the cardinal architectural violation — it breaks backend portability.

## The Pillars of Design

Load-bearing; violating one silently breaks the design (the full statement is in `Planning.md` §3):
- **Derived, not declared.** Users state reads/writes only. Any API that asks the user to specify *order* is a smell.
- **`.Writes` vs `.Blends`.** `.Writes` = produce (engine may clear). `.Blends` = accumulate onto existing contents (`LoadOp.Load` + blend; the pass is both last-reader and next-writer). Barrier derivation needs the distinction.
- **Backend isolation / portability.** All procedural, struct-heavy, API-specific code stays in the backends. Core/Graph speak in abstract resources and intents so every backend stays swappable.
- **Pure per-frame closures.** The graph is built once and cached; each `.Draw`/`.Dispatch` body runs every frame. So a closure may capture **stable objects** — engine handles and app-side stable producers (a painter, a scene) — and read their **live** members at execute time; it must **not** capture a per-frame **value** (that snapshot freezes at build). Pull per-frame facts from `ctx` and per-frame data from neutral `Constants`/`Storage` buffers or from a stable producer passed as pass state. The `Draw<TState>`/`Dispatch<TState>` overloads let a body be written as a `static` lambda so accidental per-frame capture cannot compile.
- **No allocations on the hot path.** The steady per-frame render loop allocates no managed memory (especially in Release) — preallocated/pooled buffers and `ref struct`s, and the graph is built once and cached. Applies to Vysara's per-frame tick/route/composite path too.
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

The command registered is `roslyn-mcp`. The server reads from disk on every call so the workspace is always current after a save — no reload needed between an edit and a query. The solution path for all calls is the repo-root-relative `BinaryVibrance.Kaldra.slnx` (resolve it against the current working directory so calls hit whichever worktree you're in — never a hard-coded main-worktree path).
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
