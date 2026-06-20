using Loom.Core.Primitives;

namespace Loom.Core.UI;

/// <summary>
/// A single batched draw within a <see cref="UIDrawData"/>: a contiguous run of indices sharing a primitive
/// family, texture, and scissor. The default compositor emits a new command only when one of those changes
/// (PlanningUI.md §D2.6 — atlas-everything + state-run batching), so thousands of rects/glyphs collapse into one
/// indexed draw. Mirrors an ImGui draw command (texture + clip + index range).
/// </summary>
/// <param name="Kind">The primitive family this run draws.</param>
/// <param name="TextureId">The atlas/source texture (<c>0</c> = the 1×1 white texel used by solid fills).</param>
/// <param name="ClipRect">The scissor rectangle, in surface pixels.</param>
/// <param name="IndexOffset">First index of the run (into <see cref="UIDrawData.Indices"/>).</param>
/// <param name="IndexCount">Number of indices in the run.</param>
/// <param name="VertexOffset">Value added to each index before fetching a vertex (the ImGui <c>VtxOffset</c> trick for &gt;64k vertices).</param>
public readonly record struct UIDrawCommand(
	UIPrimKind Kind, int TextureId, Rect ClipRect,
	int IndexOffset, int IndexCount, int VertexOffset);
