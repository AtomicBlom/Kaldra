using System.Globalization;
using System.Numerics;
using System.Text;

using Loom.Core;

namespace Loom.UI;

/// <summary>
/// An authoring colour — float RGBA where <c>0..1</c> is SDR / paper-white range and a component <c>&gt;1</c> is
/// above paper white (opt-in HDR). Named <c>HdrColor</c> (not <c>Color</c>) for explicitness and to dodge the
/// <see cref="System.Drawing.Color"/> clash (PlanningUI.md §Color/HDR). Folded to a premultiplied
/// <see cref="Half4"/> by the compositor; the paper-white reference scale is applied in the shader (MU1).
/// </summary>
public readonly record struct HdrColor(float R, float G, float B, float A)
{
	public static HdrColor Transparent => new(0f, 0f, 0f, 0f);

	/// <summary>From 8-bit sRGB-ish channels (0..255) at full alpha — the common HUD authoring shorthand.</summary>
	public static HdrColor FromRgb(byte r, byte g, byte b, byte a = 255) => new(r / 255f, g / 255f, b / 255f, a / 255f);

	/// <summary>Fold in a group/animation opacity and convert to a premultiplied <see cref="Half4"/> for the draw stream.</summary>
	public Half4 ToPremultiplied(float opacity = 1f) => Half4.FromStraight(R, G, B, A * opacity);
}

/// <summary>What a <see cref="SpriteVisual"/> draws. Phase B ships the rectangle family (fill + border); glyph-run
/// and image content arrive with MSDF text + images (MU1+). A small record hierarchy so the compositor switches
/// on the concrete type with no reflection.</summary>
public abstract record VisualContent;

/// <summary>A solid (optionally rounded) rectangle with an optional border — the Border/Panel background primitive.</summary>
public sealed record RectContent(Vector2 Size, HdrColor? Fill, float CornerRadius = 0f, HdrColor? BorderBrush = null, float BorderThickness = 0f) : VisualContent;

/// <summary>
/// A retained composition node (PlanningUI.md §D2.5) — the tree a pluggable <see cref="IUiCompositor"/> renders.
/// Carries a 2D <see cref="Transform"/> (animatable independently of layout), group <see cref="Opacity"/>, an
/// optional <see cref="Clip"/>, and a damage flag for incremental recomposition. Backend-neutral.
/// </summary>
public abstract class Visual
{
	private readonly List<Visual> _children = [];

	/// <summary>Affine transform relative to the parent visual (layout writes the offset; animation may write more).</summary>
	public Matrix3x2 Transform { get; set; } = Matrix3x2.Identity;

	/// <summary>Group opacity (multiplies down the subtree).</summary>
	public float Opacity { get; set; } = 1f;

	/// <summary>Optional clip rectangle in this visual's local space.</summary>
	public Rect? Clip { get; set; }

	/// <summary>Damage flag — a visual-only change marks the subtree for recomposition (MU2 incremental path).</summary>
	public bool IsDirty { get; internal set; } = true;

	public IReadOnlyList<Visual> Children => _children;

	public void Add(Visual child) { _children.Add(child); MarkDirty(); }
	public void Clear() { _children.Clear(); MarkDirty(); }

	/// <summary>Mark this visual (and implicitly its subtree, via the walk) as needing recomposition.</summary>
	public void MarkDirty() => IsDirty = true;

	/// <summary>A deterministic text form of the composition subtree — the element→visual test seam (PlanningUI.md §D8.1).</summary>
	public string Dump()
	{
		var sb = new StringBuilder();
		Dump(sb, 0);
		return sb.ToString();
	}

	private protected virtual void DescribeSelf(StringBuilder sb)
	{
		var c = CultureInfo.InvariantCulture;
		sb.Append(c, $"{GetType().Name} xform=[{Transform.M31:0.###},{Transform.M32:0.###}] op={Opacity:0.###}");
		if (Clip is { } cl) sb.Append(c, $" clip={cl}");
	}

	private void Dump(StringBuilder sb, int depth)
	{
		sb.Append(' ', depth * 2);
		DescribeSelf(sb);
		sb.Append('\n');
		foreach (var c in _children)
			c.Dump(sb, depth + 1);
	}
}

/// <summary>A grouping node with no content of its own (PlanningUI.md §D2.5).</summary>
public sealed class ContainerVisual : Visual;

/// <summary>A leaf node that draws <see cref="Content"/> (rect/border now; glyph-run/image later).</summary>
public sealed class SpriteVisual : Visual
{
	public VisualContent? Content { get; set; }

	private protected override void DescribeSelf(StringBuilder sb)
	{
		base.DescribeSelf(sb);
		if (Content is RectContent r)
		{
			var c = CultureInfo.InvariantCulture;
			sb.Append(c, $" rect={r.Size.X:0.###}x{r.Size.Y:0.###}");
			if (r.Fill is { } f) sb.Append(c, $" fill=({f.R:0.###},{f.G:0.###},{f.B:0.###},{f.A:0.###})");
			if (r.CornerRadius > 0) sb.Append(c, $" radius={r.CornerRadius:0.###}");
			if (r.BorderBrush is { } b && r.BorderThickness > 0)
				sb.Append(c, $" border={r.BorderThickness:0.###}({b.R:0.###},{b.G:0.###},{b.B:0.###},{b.A:0.###})");
		}
	}
}

/// <summary>The root of one surface's composition tree (PlanningUI.md §D2.5).</summary>
public sealed class RootVisual : Visual
{
	public Vector2 SurfaceSize { get; set; }
	public bool IsHdrSurface { get; set; }
}

/// <summary>The portability boundary (PlanningUI.md §D2.5): renders a surface's <see cref="RootVisual"/> tree.
/// Loom ships <see cref="DrawListCompositor"/>; a foreign engine substitutes its own and consumes the tree directly.</summary>
public interface IUiCompositor
{
	/// <summary>Turn one surface's composition tree into rendering.</summary>
	void Composite(RootVisual root, UiSurfaceId surface);
}
