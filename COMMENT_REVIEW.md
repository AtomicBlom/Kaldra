# Loom — Comment & Documentation Review

**Date:** 2026-06-28 — re-verified against HEAD `c1c57eb`.
**Standard (the owner's):** a comment describes *what the code does or why* and is **self-contained** — never a milestone (`M13`, `MU4`), a planning-doc section (`§D1.2`, `Planning.md §8`), or project-history/schedule narration (`for now`, `previously`, `lands in MU5`). XML docs on public APIs and genuine algorithm/gotcha explanations are wanted.

---

## Verdict

**Not met — a large but mechanical sweep remains, and it is now urgent.** The planning-doc rewrite deletes `PlanningPhase2.md` and `PlanningUI.md`, which turns every in-source citation of them into a **dangling reference**. Stripping those citations (the self-contained rule forbids them outright) is the bulk of the remaining work.

Current offending-tag counts across `.cs`:

| Category | Pattern | ~Count | Hotspots |
|---|---|---|---|
| Milestone / phase | `M\d`, `M7-B4`, `MU\d`, `Phase`, `slice` | ~80 | `external/SUIsei`, `Loom.Vulkan` |
| Planning-section | `§` | ~142 | `external/SUIsei` (XML docs), `Loom.Vulkan` |
| Planning-doc citation | `PlanningUI.md` / `Planning.md` / `PlanningPhase2.md` | ~150 | `external/SUIsei` (overwhelmingly) |
| Temporal narration | `for now`, `previously`, `no longer`, `lands in`, … | ~21 | scattered (some legitimate) |

---

## Priority 1 — dangling references created by this change (must fix)

Deleting `PlanningPhase2.md` and `PlanningUI.md` strands their citations. **Strip them — do not repoint** (the self-contained rule forbids the citation entirely):

- **~130 source files + ~30 test files in `external/SUIsei`** carry `PlanningUI.md §…` in XML docs / inline comments (the `DependencyObject`/`DependencyProperty` heads, the whole `Controls/` + `Animation/` + `Input/` set, and most of `SUIsei.Tests`). The new `external/SUIsei/Planning.md` explicitly states the source must not reference it by section.
- `Milestone16Notes.md` cites `PlanningPhase2.md §5/§6` and `Planning.md §8`; `external/SUIsei/MUxDecisions.md` cites `PlanningUI.md §D10`. (`tools/generate-llms-docs.cs` already strips these at doc-gen time, but the source citations remain.)

---

## Priority 2 — cited stale / archaeology comments (targeted edits)

- `Loom.DirectX12/DirectX12RenderContext.cs:23` — "Compute and ImGui land in following slices." Compute is implemented; drop the sentence.
- `Loom.Vulkan/VulkanRenderContext.cs:~82` and `:~316` — "…the old interpolated-string key heap-allocated on every draw" / "…replacement for the old interpolated-string pipeline-cache key." State the invariant ("the key is a value type, so the per-draw lookup allocates nothing"); drop the archaeology.
- `external/SUIsei/SUIsei/DependencyObject.cs:4,16,70,114` and `DependencyProperty.cs:12,47,55` — `(PlanningUI.md §D1.x)` / `(MU4 correctness)` tails on otherwise-good explanations: strip the tag, keep the prose.

---

## Priority 3 — the mechanical milestone/section strip

The remaining ~200 hits are each a tail or parenthetical on an otherwise self-contained sentence — amputate the clause. Heaviest concentration: `external/SUIsei` (MU/§ tags throughout the control, animation, and test files), then `Loom.Vulkan` (`VulkanEngine.cs`, `VulkanMeshStore.cs`, `VulkanContext.cs`, `VulkanEngine.FrameRecord.cs` carry `M7-B4`/`M12`/`§7.4`/`§7.6`).

---

## Doc-coverage gaps (public, author-facing — fill these)

- `Loom.Vulkan/VulkanSwapchain.cs:15-21` — `Handle`/`ImageFormat`/`ColorSpace`/`Extent`/`Images`/`ImageViews`/`ImageCount` all bare.
- `Loom.DirectX12/DirectX12Engine.cs` — `Render`/`Resize`/`WaitIdle`/`CreateMesh`/`CreateTexture`/`CreateMaterial` undocumented (while `EnableImGui` beside them is).
- `Loom.Assets/AssetWatcher.cs` — `Dispose` undocumented.

### Use proper XML-doc structure

When filling the gaps above — or rewriting any XML doc during the sweep (e.g. the `DependencyObject`/`DependencyProperty` summaries the `§D1.2` strip touches) — write real doc-comment markup, not a bare sentence:

- **`<summary>`** — the one-line what/why, on every public member.
- **`<remarks>`** — non-obvious detail, a gotcha, or a "why this and not that"; keep it out of the summary.
- **`<param>` / `<typeparam>` / `<returns>`** — one per parameter, type parameter, and return value.
- **`<list type="bullet">` / `<list type="table">`** — for an enumeration of cases or options, rather than a comma-run buried in the summary.
- **`<see cref="…"/>` / `<paramref name="…"/>`** — for every type/member/parameter reference, so it links in IDEs and survives renames.
- **`<c>…</c>` / `<code>…</code>`** — inline vs block code.

The self-contained rule still applies: structure the doc properly, but keep milestone / section / `PlanningUI.md` tags out of it.

---

## Already resolved / moot

- `UIPlacement.cs` no longer exists; `mesh.slang` carries no milestone tag (only the dead-output concern remains — tracked in [`CODE_REVIEW.md`](CODE_REVIEW.md)).
- The high-value gotchas are intact and tag-free — **preserve them**: the clip-space / Y-flip explanation, the scRGB HDR rationale, glTF tangent handedness + Gram-Schmidt guard, the "retire, don't free" mid-frame growth note, and the structured-buffer `GetDimensions` trap.

---

## Realistic remaining scope

**~200 edits, almost all one-line clause strips** — and ~160 of those are the `external/SUIsei` `PlanningUI.md §…` citations made dangling by this change. Best done as a single mechanical sweep, finished with a grep that confirms zero `Planning*.md` / `§` / `M\d` / `MU\d` tags remain in any `.cs`.
