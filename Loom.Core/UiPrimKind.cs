namespace Loom.Core;

/// <summary>
/// The primitive family a <see cref="UiDrawCommand"/> draws — selects the branch the single <c>ui.slang</c>
/// fragment switches on (PlanningUI.md §D2.7). A flat <see cref="byte"/> so it rides the command cheaply.
/// </summary>
public enum UiPrimKind : byte
{
	/// <summary>A solid axis-aligned fill (white-texel sampled).</summary>
	Fill,

	/// <summary>An analytic rounded rectangle (radii in <see cref="UiVertex.Params"/>).</summary>
	RoundedRect,

	/// <summary>An analytic (optionally rounded) border stroke (half-width in <see cref="UiVertex.Params"/>).</summary>
	Border,

	/// <summary>An MSDF glyph sampled from a font atlas page.</summary>
	Glyph,

	/// <summary>A tinted image sampled from a UI image atlas or an in-world source target.</summary>
	Image,
}