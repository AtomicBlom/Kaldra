# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What Loom is

Loom is a declarative, render-graph-driven rendering engine for .NET 10. You declare *what* each pass reads and writes; the engine derives ordering, barriers, image-layout transitions, transient memory aliasing, and CPU↔GPU sync. The procedural, struct-heavy graphics-API code is quarantined in a backend layer and never leaks into the authoring API.

**`Planning.md` is the design bible and the source of truth — read it before any non-trivial work.** It is long but authoritative: architecture (§2), platform/deps (§3), the canonical API vocabulary (§5), the reference frame (§6), the effect cookbook (§7), the load-bearing design principles (§8), and the milestone roadmap (§12). This file is the operational layer on top of it: how the code is written, where the project is now, and the invariants that must survive a context reset.

## Current state

**Milestone 0 is essentially complete.** The nine implementation cards are done: a clear-to-color Vulkan window runs via dynamic rendering on a Vulkan 1.3 device, validation-clean. The one remaining M0 card is the shader-pipeline *decision* (Slang vs GLSL, offline vs runtime) — it spans M0–1 and is the natural M1 kickoff. Next milestone proper is M1 (marshaling seam + hand-baked triangle).

What M0 delivered:
- Dependency chain wired `Sandbox → Vulkan → Graph → Core`. `Loom.Core` is a library; `Loom.Sandbox` is the only `Exe`; all projects target `net10.0`; the solution is `Loom.slnx`.
- Silk.NET **2.23.0**: Vulkan + `Extensions.KHR`/`Extensions.EXT` + Maths in `Loom.Vulkan`; Windowing + Input in `Loom.Sandbox`. The Vulkan stack stays in `Loom.Vulkan`; the host hands the backend the window's `IVkSurface`, so `Loom.Vulkan` has **no** `Silk.NET.Windowing` reference.
- `Loom.Vulkan`: `VulkanContext` (instance + debug messenger + surface + 1.3 device with dynamic rendering / synchronization2 / timeline semaphores), `Swapchain` (format + present-mode selection, recreate-on-resize), `Renderer` (2 frames in flight, `vkCmdBeginRendering` + `vkCmdPipelineBarrier2` + `vkQueueSubmit2` — no renderpass/framebuffer objects).

Dev-environment notes:
- This dev machine is **Windows-on-ARM (Snapdragon X / Adreno X1-85)** — don't assume x64.
- **Vulkan SDK 1.4.350.0** is installed (validation layers available). Note: `winget` can't install it from a non-interactive shell (the UAC elevation gets cancelled) — install from an interactive elevated terminal.
- A benign `[vk:WARN]` about duplicate `VK_LAYER_MSFT_driver_sorting` manifests (Microsoft `D3DMappingLayers`) prints at startup; it's a loader/environment quirk, not a Loom issue.
- `Loom.Sandbox` honors `LOOM_SMOKE_FRAMES=N` — render N frames then exit, for headless verification.

## Progress tracking

Milestone progress is tracked on the **GitHub Projects (v2) board "Loom – Render Graph Engine"** (board #6, owner `AtomicBlom`, linked to `AtomicBlom/Loom`): <https://github.com/users/AtomicBlom/projects/6>. `Planning.md` §12 remains the source of truth for *what* each milestone is; the board is the live status of the work.

- **One board, draft-issue cards**, grouped by two custom single-select fields: **Stage** (`M0`…`M6`, mapping to the Planning.md §12 milestones) and **Kind** (`Implementation` / `Cross-cutting` / `Decision`). Status is the usual `Todo` / `In Progress` / `Done`.
- **M0–M1 are broken into task cards; M2–M6 are one tracking card each** (carrying that milestone's deliverable checklist). Expand a later milestone into task cards when work on it begins.
- **Keep the board current as you work:** move a card to **In Progress** when you start it and to **Done** once its acceptance bar is met; check off the sub-tasks in the card body as they land. When a milestone needs a choice, that's a `Decision` card — resolve it (and record the decision) before the implementation cards it gates.
- The board needs the `gh` `project` scope (plain `repo` scope is not enough). Add/edit cards with `gh project item-create` / `gh project item-edit` against board number `6`, owner `AtomicBlom`.

## Commands

    dotnet build Loom.slnx                 # build the whole solution
    dotnet run --project Loom.Sandbox      # run the sandbox (the runnable sample / demo)
    dotnet build Loom.Core                 # build a single project
    dotnet format Loom.slnx                # apply the .editorconfig conventions

No test project exists yet. Milestone 2 adds unit tests for the describe layer; create a test project then and use `dotnet test --filter <FullyQualifiedName~Name>` for single-test runs.

## Coding conventions

Enforced by `.editorconfig` at the repo root; the essentials:
- **File-scoped namespaces** (`namespace Loom.Core;`), never block-scoped.
- **Tabs, not spaces** for `.cs` indentation (width 4). Markup/config (`.csproj`, `.slnx`, `.json`) uses 2-space indent.
- **Braces where appropriate** — required around multi-line blocks; a single-line `if (cond) return;` may omit them.
- **Modern .NET / C#** — reach for the current idiom when it reads cleaner: `var` when the type is apparent, target-typed `new`, collection expressions, pattern matching / switch expressions, primary constructors, expression-bodied members, `using` declarations, null-propagation and coalescing. Nullable reference types and implicit usings are on.
- Allman braces; `System.*` usings sorted first; usings outside the namespace.

## Goals & constraints

- **Cross-platform from the start.** Windows, Linux, and macOS. Silk.NET, Vulkan, and the windowing/input stack are cross-platform — keep OS-specific APIs out of the engine (or behind a capability check). Don't bake in Win32-only assumptions.
- **Backend-portable by design (future DX12).** Vulkan is the *first* backend, not the only one — a DX12 backend may follow. This raises the stakes on the backend-isolation invariant below: it isn't tidiness, it's what lets a second backend slot in without touching the describe/execute layers.

## Architecture in one screen

Three layers, mapped to projects and kept strictly separate — this separation is what makes the design tractable, so protect it:

- **Describe** (`Loom.Core`) — immutable, hashable, fluent builders: `GraphBuilder`, `ResourceHandle`, `Domain`, `PassBuilder`, technique builders. The only layer authored against directly; allocates freely.
- **Execute** (`Loom.Graph`) — per frame: topological sort from the read/write edges, *derive* the barrier/layout-transition and aliasing decisions, drive command recording. Produces **backend-neutral** scheduling decisions (no Vulkan types) so the same plan can target Vulkan or a future DX12 backend.
- **Bake + record** (`Loom.Vulkan`) — the *first backend*. Marshals descriptions transiently to native structs, creates pipelines/descriptor layouts (cached by hash), and translates Execute's neutral decisions into real GPU work (`vkCmdPipelineBarrier2`, dynamic rendering, etc.). The "marshaling seam" — the only place procedural graphics-API code lives. (`vkCreate*` copies its inputs, so the struct soup only exists transiently in pinned native memory, then is freed.)
- `Loom.Shaders` — build step producing SPIR-V + reflection metadata consumed at bake time.

**Dependency direction: `Sandbox → Vulkan → Graph → Core`.** `Loom.Core` and `Loom.Graph` carry **no** Vulkan/Silk.NET reference. Introducing a Vulkan (or any graphics-API) type into Core or Graph is the cardinal architectural violation — it forecloses the DX12 backend.

## Invariants to protect (Planning.md §8)

Load-bearing; violating them silently breaks the design:
- **Derived, not declared.** Users state reads/writes only. Any API that asks the user to specify *order* is a smell.
- **`.Writes` vs `.Blends`.** `.Writes` = produce (engine may clear). `.Blends` = accumulate onto existing contents (`LoadOp.Load` + blend; the pass is both last-reader and next-writer). Barrier derivation needs the distinction.
- **Backend isolation / portability.** All procedural, struct-heavy, API-specific code stays in the backend (`Loom.Vulkan`). Core/Graph speak in abstract resources and intents so a DX12 backend can join later.
- **Pure per-frame closures.** `.Draw` / `.Dispatch` callbacks pull from `ctx` (`ctx.Scene`, `ctx.Camera`, `ctx.DeltaTime`) instead of capturing, keeping the graph description frame-independent and cacheable.
- **Ping-pong via handle swap.** Two physical resources + a `(read, write)` tuple swapped in a normal loop; the graph follows the handles (bloom, JFA).
- **Vulkan 1.3 baseline.** Dynamic rendering, synchronization2, and timeline semaphores are enabled as device features — no renderpass/framebuffer objects; emit `vkCmdPipelineBarrier2`.

## Agent Behaviour
A note on using image captures to determine whether something has worked or not, images take up a LOT of context
When verifying whether an image is correct or not, use a subagent and tell it what you're expecting the image to contain in as much detail as possible, the sub-agent should return whether it met your expectations or not.