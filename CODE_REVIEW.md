# Loom — Code Review

**Date:** 2026-06-28 — re-verified against HEAD `c1c57eb` on `Review`.
**Method:** Multi-agent, Roslyn-backed semantic re-audit. Every prior finding was re-opened in the current tree; the earlier cleanup sprint was checked for regressions; the forward-looking surface (backends, advanced GPU features, portability) was re-assessed.
**Scope:** all engine, backend, assets, and UI projects.

This is a **current-state snapshot.** Resolved findings from earlier reviews are intentionally not relisted. Comment-hygiene items are tracked separately in [`COMMENT_REVIEW.md`](COMMENT_REVIEW.md); the engine-capability roadmap lives in [`Planning.md`](Planning.md) §5.

---

## Verdict

Loom is **architecturally healthy at its core**, and the prior cleanup sprint's goals are **met against current HEAD** — the tree is, if anything, slightly ahead of the previous review's recorded status. The cardinal rule (no graphics-API type in `Loom.Core`/`Loom.Graph`) is verified clean, the Describe/Execute/Backend separation holds, and backends fail loud on unmapped enum values rather than silently degrading.

- **Every prior "top priority" correctness/parity fix is present and unregressed** — DX12 descriptor-ring overflow throws (no mid-frame wrap), sRGB mips average in linear light, async-compute throws at derive, the bake-cache hash includes buffer `ByteSize`, glTF bakes node world transforms (inverse-transpose normals), the DXIL build hard-fails on a declared stage's failure, and an automated cross-backend parity test ships.
- **Three findings the prior review still listed open are in fact fixed:** DP coercion now applies to the *resolved* value; `Persistent(size>1)` now **throws** (no silent history-ring alias); `ImageLoader` wraps decode errors with the asset path.
- **Backend isolation is clean**, and the prior "native ImGui `nint` caveat" is superseded — the engine's UI path is the pointer-free `DrawList` verb; all ImGui pointers are confined to the opt-in app-side `Loom.UI.ImGui` bridge.

What remains is a short list of low-risk open items, plus the forward-looking engine gaps (un-built capabilities, not bugs).

---

## Open findings (low-risk)

| Severity | Finding | Location | Fix |
|---|---|---|---|
| 🟡 Minor | `VertexLayout` offsets/stride ignore attribute alignment (safe while all formats are 4-byte-granular) | `Loom.Core/Geometry/VertexLayout.cs` | round each offset up to the format alignment before a non-4-byte format lands |
| 🟡 Minor | Grid Auto-then-Star measure ordering can disagree with WinUI's fixed→auto→star single pass (low risk until elastic/wrapping content) | `external/SUIsei/SUIsei/Controls/Grid.cs` | reorder to fixed→auto→star |
| 🟡 Minor | Bootstrap `DirectX12Triangle` (root-sig + PSO + FXC) is allocated for the engine's whole lifetime, used only on the `Graph is null` fallback path | `Loom.DirectX12/DirectX12Engine.cs`, `DirectX12Triangle.cs` | create lazily, or drop once a graph is always set |
| ⚪ Note | Backends have zero CPU unit tests — the marshaling / hash-key / alias-offset logic is covered only by GPU goldens (CI-skipped) | `Loom.Tests` | extract the device-free slices behind plain types + a backend-logic test project |
| ⚪ Note | No compute→compute / ping-pong barrier-derivation test (the invariant is demo-exercised, not pinned) | `Loom.Tests/BarrierPlanTests.cs` | add a `ReadWrites` WAW test + a handle-swap test |
| ⚪ Note | `mesh.slang` is compiled but referenced by no technique — dead build output | `Loom.Sandbox/Shaders/mesh.slang` | delete it |
| ⚪ Note | One SourceGen diagnostic uses a zero-length `TextSpan` (squiggle lands on the line with no range) | `external/SUIsei/SUIsei.SourceGen/XamlSourceGenerator.Resolution.cs` | span the offending token |

---

## Forward-looking gaps (engine roadmap, not bugs)

Honest, additive gaps — **none is an irreversible dead-end**, and each is either absent or throws loudly rather than lying. These are the engine-capability roadmap ([`Planning.md`](Planning.md) §5.1), not defects:

- **Async compute / multi-queue** — authorable but throws at derive (single-timeline scheduler, one queue per backend).
- **Indirect / GPU-driven draw** — no `DrawIndirect`/count-buffer verb; every draw & instance count is a CPU `int`.
- **Renderable cube / array / 3D / mipped targets** — graph targets are single-layer single-mip 2D (loading cube/HDR *source* textures already works).
- **Temporal stack** — `Persistent(size>1)` throws; no previous-frame view or motion-vector channel.
- **MSAA resolve** — `Domain.Samples` is dead-plumbed on Vulkan, ignored on DX12.
- **Bindless / typed storage** — one descriptor per reflected binding (`DescriptorCount=1`); `Storage` collapses to one `GENERAL` read-write state (the one **WebGPU**-relevant friction).
- **Streaming / partial residency** — asset upload is synchronous and fully resident.

---

## Positive confirmations (verified, not assumed)

- Backend isolation clean across Core, Graph, Assets, UI — no `Vk*`/`D3D12*`/`Marshal`/pointer leak; the only `stackalloc` is a CPU PNG encoder.
- Neutral RH, +Y-up, **z ∈ [0,1]** matrix convention with a single `ClipSpaceCorrection` seam — Metal/WebGPU depth range already satisfied; clip fix-up overridable per backend.
- Opaque `object surface` + `EngineSettings` backend-creation seam; backends self-register guarded by an `IsSupported()` probe.
- `BarrierPlan`/`AliasPlan` advisory (a WebGPU auto-sync backend can ignore both); per-attachment Load/Store derived (tile-based / Metal-memoryless friendly); the full dependency DAG is preserved (`ScheduledPass.DependsOn`).
- Over-promised APIs (async-compute, multi-frame history ring) and same-pass sample+write all throw at derive — loud, not silent.
