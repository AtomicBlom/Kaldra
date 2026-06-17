# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What Loom is

Loom is a declarative, render-graph-driven rendering engine for .NET 10. You declare *what* each pass reads and writes; the engine derives ordering, barriers, image-layout transitions, transient memory aliasing, and CPU↔GPU sync. The procedural, struct-heavy graphics-API code is quarantined in a backend layer and never leaks into the authoring API.

**`Planning.md` is the design bible and the source of truth — read it before any non-trivial work.** It is long but authoritative: architecture (§2), platform/deps (§3), the canonical API vocabulary (§5), the reference frame (§6), the effect cookbook (§7), the load-bearing design principles (§8), and the milestone roadmap (§12). This file is the operational layer on top of it: how the code is written, where the project is now, and the invariants that must survive a context reset.

## Current state

**Milestones M0–M11 are complete; M9 (deferred generalization) is partial.** Loom runs a full reference frame on **two backends at pixel parity — Vulkan and DX12**. **M11 (asset & material foundation) shipped:** texture loading + CPU-generated mips, a `Material` / multi-texture binding abstraction, tangent-space normal mapping in the deferred path, the neutral `Loom.Assets` project (StbImageSharp image + SharpGLTF model loaders), and normal-map + glTF reference demos at cross-backend parity. The next work is the **M12–M16** roadmap (geometry generalization + debug spine, then the three game-archetype tracks), tracked on the board.

What's shipped:
- **Projects:** `Loom.Core` (Describe, library) → `Loom.Graph` (Execute) → `Loom.Vulkan` + `Loom.DX12` (backends); `Loom.Assets` (neutral image/glTF loaders, beside Core); `Loom.Shaders` (build step); `Loom.Tests` (xUnit, graph-layer + mip tests); `Loom.Sandbox` (the only `Exe`, demos). All target `net10.0`; the solution is `Loom.slnx`.
- **Host/windowing:** Silk.NET **2.23.0** (Vulkan + `Extensions.KHR`/`Extensions.EXT` + Maths in `Loom.Vulkan`; Windowing + Input in `Loom.Sandbox`). The host hands the backend the window's `IVkSurface`, so the backends carry **no** `Silk.NET.Windowing` reference.
- **Describe layer:** `GraphBuilder`/`PassBuilder` with `.Writes`/`.Blends`/`.Samples`/`.ReadWrites`/`.WritesDepth`/`.ReadsDepth`/`.ReadsVertex`; typed handles (`ColorHandle`/`DepthHandle`/`BufferHandle`); fixed + backbuffer-relative `Domain`s; persistent + buffer resources; present/readback. Immutable + hashable (drives the bake cache).
- **Execute layer:** topological scheduling, derived barriers/layout transitions, transient memory aliasing, lifetime analysis — all backend-neutral.
- **Shipped effects (8):** deferred G-buffer + multi-light lighting, single shadow map, SSAO + blur, forward alpha-blend transparency, GPU particles (compute→vertex), JFA outlines + GPU→CPU picking readback, bloom pyramid, auto-exposure + tonemap. Plus HDR (scRGB) output, low-res world domain + upscale, ImGui HUD. `FullReferenceDemo` composes them all.
- **Assets & materials (M11):** `Loom.Assets` decodes images (StbImageSharp) and glTF models (SharpGLTF) into neutral `Loom.Core` descriptions; `TextureData` builds the mip chain on the CPU (deterministic box filter → byte-identical across backends); each backend has a `TextureStore` (multi-mip upload) + `MaterialStore`; a `Material` fills a technique's reflected sampler slots via `ctx.Draw(mesh, material, technique)`; `Vertex` carries a tangent; `gbuffer_normalmap.slang` does tangent-space normal mapping into the existing deferred G-buffer. Demos: `NormalMappingDemo`, `GltfDemo`.
- **Shaders:** offline Slang → SPIR-V (Vulkan) + signed DXIL (DX12); reflection-derived descriptor layouts.
- **Backend baseline (both):** dynamic rendering / no renderpass objects, sync2-style barriers, 2 frames in flight, Vulkan 1.3 device features (dynamic rendering / synchronization2 / timeline semaphores); DX12 mirrors via fences + PSOs + placed-resource aliasing.

**The gap (what M12+ adds):** M11 delivered texture/model loading, materials, and normal mapping. Still missing — the rest of the asset/scene/geometry half: per-technique vertex layouts, instancing/indirect, a `Scene` draw-list + culling, the debug/profiling spine, and the per-track features (sprites/2D, skinning/CSM/sky, PBR/terrain/GPU-driven). That's the M12–M16 roadmap.

Dev-environment notes:
- This dev machine is **Windows-on-ARM (Snapdragon X / Adreno X1-85)** — don't assume x64.
- **Vulkan SDK 1.4.350.0** is installed (validation layers available). Note: `winget` can't install it from a non-interactive shell (the UAC elevation gets cancelled) — install from an interactive elevated terminal.
- A benign `[vk:WARN]` about duplicate `VK_LAYER_MSFT_driver_sorting` manifests (Microsoft `D3DMappingLayers`) prints at startup; it's a loader/environment quirk, not a Loom issue.
- `Loom.Sandbox` honors `LOOM_SMOKE_FRAMES=N` (render N frames then exit) and `LOOM_CAPTURE=path` (capture one frame to PNG with frozen time + fixed delta) — for headless verification and deterministic cross-backend pixel diffing.

## Progress tracking

Milestone progress is tracked on the **GitHub Projects (v2) board "Loom – Render Graph Engine"** (board #6, owner `AtomicBlom`, linked to `AtomicBlom/Loom`): <https://github.com/users/AtomicBlom/projects/6>. `Planning.md` §12 remains the source of truth for *what* each milestone is; the board is the live status of the work.

- **One board, draft-issue cards**, grouped by two custom single-select fields: **Stage** (`M0`…`M16`; M0–M10 map to Planning.md §12, M11–M16 are the asset/scene + game-track roadmap) and **Kind** (`Implementation` / `Cross-cutting` / `Decision`). Status is the usual `Todo` / `In Progress` / `Done`.
- **Milestones are a mix of tracking and task cards;** M0–M1, M7, and the whole **M11–M16** roadmap are broken into per-task cards, while some earlier milestones carry a single tracking card with a deliverable checklist. Expand any tracking-only milestone into task cards when work on it begins. **M16 is an umbrella** that should split into sub-milestones when started.
- **Stage colours encode done/not-done:** **GREEN = done**, **ORANGE = not done**. Keep new/in-flight stages orange and flip to green when shipped.
- **Keep the board current as you work:** move a card to **In Progress** when you start it and to **Done** once its acceptance bar is met; check off the sub-tasks in the card body as they land. When a milestone needs a choice, that's a `Decision` card — resolve it (and record the decision) before the implementation cards it gates.
- The board needs the `gh` `project` scope (plain `repo` scope is not enough). Add/edit cards with `gh project item-create` / `gh project item-edit` against board number `6`, owner `AtomicBlom`. ⚠️ **Editing the Stage option set via `updateProjectV2Field` regenerates every option ID** (GitHub does not match by name), which orphans existing cards' Stage values — back up item→Stage assignments first, then remap by name.

## Commands

    dotnet build Loom.slnx                 # build the whole solution
    dotnet run --project Loom.Sandbox      # run the sandbox (the runnable sample / demo)
    dotnet build Loom.Core                 # build a single project
    dotnet format Loom.slnx                # apply the .editorconfig conventions

Tests live in `Loom.Tests` (xUnit) and cover the describe/execute layers — scheduling, barrier derivation, aliasing, lifetimes, graph hashing. Run a single test with `dotnet test --filter <FullyQualifiedName~Name>`. There are **no golden-image / rendered-output tests yet** — that harness is an M13 card (the manual loop today is `LOOM_CAPTURE` + frozen time + cross-backend pixel diff).

## Coding conventions

Enforced by `.editorconfig` at the repo root; the essentials:
- **File-scoped namespaces** (`namespace Loom.Core;`), never block-scoped.
- **Tabs, not spaces** for `.cs` indentation (width 4). Markup/config (`.csproj`, `.slnx`, `.json`) uses 2-space indent.
- **Braces where appropriate** — required around multi-line blocks; a single-line `if (cond) return;` may omit them.
- **Modern .NET / C#** — reach for the current idiom when it reads cleaner: `var` when the type is apparent, target-typed `new`, collection expressions, pattern matching / switch expressions, primary constructors, expression-bodied members, `using` declarations, null-propagation and coalescing. Nullable reference types and implicit usings are on.
- Allman braces; `System.*` usings sorted first; usings outside the namespace.

## Goals & constraints

- **Cross-platform from the start.** Windows, Linux, and macOS. Silk.NET, Vulkan, and the windowing/input stack are cross-platform — keep OS-specific APIs out of the engine (or behind a capability check). Don't bake in Win32-only assumptions.
- **Backend-portable by design.** Vulkan was the first backend; **DX12 is now a second backend at pixel parity** (`Loom.DX12`). This makes the backend-isolation invariant load-bearing in practice, not just in principle: every new GPU feature must land in (or be `<Use…>`-gated for) both backends, and no graphics-API type may enter the describe/execute layers.

## Architecture in one screen

Three layers, mapped to projects and kept strictly separate — this separation is what makes the design tractable, so protect it:

- **Describe** (`Loom.Core`) — immutable, hashable, fluent builders: `GraphBuilder`, `ResourceHandle`, `Domain`, `PassBuilder`, technique builders. The only layer authored against directly; allocates freely.
- **Execute** (`Loom.Graph`) — per frame: topological sort from the read/write edges, *derive* the barrier/layout-transition and aliasing decisions, drive command recording. Produces **backend-neutral** scheduling decisions (no graphics-API types) so the same plan targets Vulkan or DX12.
- **Bake + record** (`Loom.Vulkan` and `Loom.DX12`) — the *backends*. Marshal descriptions transiently to native structs, create pipelines/descriptor layouts (cached by hash), and translate Execute's neutral decisions into real GPU work (Vulkan: `vkCmdPipelineBarrier2`, dynamic rendering; DX12: resource barriers, PSOs, placed-resource aliasing). The "marshaling seam" — the only place procedural graphics-API code lives. (`vkCreate*` / D3D12 create calls copy their inputs, so the struct soup only exists transiently in pinned native memory, then is freed.)
- `Loom.Shaders` — build step producing SPIR-V + signed DXIL + reflection metadata consumed at bake time.

**Dependency direction: `Sandbox → {Vulkan, DX12} → Graph → Core`.** `Loom.Core` and `Loom.Graph` carry **no** Vulkan/DX12/Silk.NET reference. Introducing a graphics-API type into Core or Graph is the cardinal architectural violation — it breaks backend portability.

## Invariants to protect (Planning.md §8)

Load-bearing; violating them silently breaks the design:
- **Derived, not declared.** Users state reads/writes only. Any API that asks the user to specify *order* is a smell.
- **`.Writes` vs `.Blends`.** `.Writes` = produce (engine may clear). `.Blends` = accumulate onto existing contents (`LoadOp.Load` + blend; the pass is both last-reader and next-writer). Barrier derivation needs the distinction.
- **Backend isolation / portability.** All procedural, struct-heavy, API-specific code stays in the backends (`Loom.Vulkan`, `Loom.DX12`). Core/Graph speak in abstract resources and intents so both backends stay swappable (and a third could join).
- **Pure per-frame closures.** `.Draw` / `.Dispatch` callbacks pull from `ctx` (`ctx.Camera`, `ctx.Time`, `ctx.DeltaTime`, `ctx.SunView`, `ctx.HoverId`) instead of capturing, keeping the graph description frame-independent and cacheable. (A first-class `ctx.Scene` draw-list is the M12 target; demos submit meshes directly today.)
- **Ping-pong via handle swap.** Two physical resources + a `(read, write)` tuple swapped in a normal loop; the graph follows the handles (bloom, JFA).
- **Vulkan 1.3 baseline.** Dynamic rendering, synchronization2, and timeline semaphores are enabled as device features — no renderpass/framebuffer objects; emit `vkCmdPipelineBarrier2`.

## Agent Behaviour
A note on using image captures to determine whether something has worked or not, images take up a LOT of context
When verifying whether an image is correct or not, use a subagent and tell it what you're expecting the image to contain in as much detail as possible, the sub-agent should return whether it met your expectations or not.