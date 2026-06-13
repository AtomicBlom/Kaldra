# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What Loom is

Loom is a declarative, render-graph-driven rendering engine for .NET 10. You declare *what* each pass reads and writes; the engine derives ordering, barriers, image-layout transitions, transient memory aliasing, and CPU↔GPU sync. The procedural, struct-heavy graphics-API code is quarantined in a backend layer and never leaks into the authoring API.

**`Planning.md` is the design bible and the source of truth — read it before any non-trivial work.** It is long but authoritative: architecture (§2), platform/deps (§3), the canonical API vocabulary (§5), the reference frame (§6), the effect cookbook (§7), the load-bearing design principles (§8), and the milestone roadmap (§12). This file is the operational layer on top of it: how the code is written, where the project is now, and the invariants that must survive a context reset.

## Current state

The repo is a **bare scaffold — pre-Milestone 0.** Five projects exist with only `.csproj` files: no hand-written source, no shaders, no package references, no `<ProjectReference>`s. Per the roadmap, Milestone 0 is next: wire the dependency chain and Silk.NET packages, then stand up a clear-to-color Vulkan window using dynamic rendering.

Scaffold notes to fix/know during Milestone 0:
- `Loom.Core.csproj` has `<OutputType>Exe</OutputType>` — a `dotnet new console` leftover. `Loom.Core` should be a **library**; `Loom.Sandbox` is the only executable.
- No `<ProjectReference>`s yet. Intended direction: `Sandbox → Vulkan → Graph → Core`, with `Loom.Shaders` feeding SPIR-V to the bake step.
- All projects target `net10.0` (matches Planning.md §3). The `net9.0` line in Planning.md Milestone 0 is stale — use **net10.0**.
- The solution is `Loom.slnx` (new XML format), not `Loom.sln` as drawn in Planning.md §11.

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
