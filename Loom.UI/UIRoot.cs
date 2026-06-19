using System.Globalization;
using System.Numerics;
using System.Text;

using Loom.Core;

namespace Loom.UI;

/// <summary>
/// One UI surface (PlanningUI.md §D2.4): owns the element <see cref="Content"/> tree and the composition
/// <see cref="Visual"/> tree it composites through a pluggable <see cref="IUICompositor"/>. Holds <b>no</b> engine
/// or backend handle, so <see cref="LayoutPass"/> + <see cref="Render"/> run to completion headless — input crosses
/// in as a size, the product is neutral <see cref="UiDrawData"/> (the headless-testable invariant, §D0/§D8).
/// </summary>
public sealed class UIRoot
{
	private readonly IUICompositor _compositor;

	public UIRoot(UiSurfaceId surface, IUICompositor? compositor = null)
	{
		Surface = surface;
		_compositor = compositor ?? new DrawListCompositor();
	}

	/// <summary>The surface this root composites into.</summary>
	public UiSurfaceId Surface { get; }

	/// <summary>The authoring (element) tree.</summary>
	public UIElement? Content { get; set; }

	/// <summary>The composition tree this root renders (PlanningUI.md §D2.5).</summary>
	public RootVisual Visual { get; } = new();

	/// <summary>True for an HDR surface (drives colour linearization downstream).</summary>
	public bool IsHdrSurface { get => Visual.IsHdrSurface; set => Visual.IsHdrSurface = value; }

	/// <summary>DIP→pixel scale: window DPI/96 for an overlay, author-chosen texel density for an in-world quad
	/// (PlanningUI.md §D2.8). Layout runs in DIPs; the render walk scales positions by this.</summary>
	public float LayoutScale { get; set; } = 1f;

	/// <summary>Measure + arrange the element tree against the surface (in pixels), then sync the composition tree.
	/// Layout happens in DIPs (<c>pixels / LayoutScale</c>); the root visual carries the DIP→pixel scale.</summary>
	public void LayoutPass(Size availablePixels)
	{
		Visual.SurfaceSize = new Vector2(availablePixels.Width, availablePixels.Height);
		Visual.Transform = Matrix3x2.CreateScale(LayoutScale);
		Visual.Clear();

		if (Content is null)
			return;

		var availDip = new Size(availablePixels.Width / LayoutScale, availablePixels.Height / LayoutScale);
		Content.Measure(availDip);
		Content.Arrange(new Rect(0f, 0f, availDip.Width, availDip.Height));
		Visual.Add(Content.Visual);
	}

	/// <summary>Composite the current visual tree to neutral <see cref="UIDrawData"/> (default-path convenience —
	/// returns <see cref="UIDrawData.Empty"/> when a foreign compositor is in use).</summary>
	public UIDrawData Render()
	{
		_compositor.Composite(Visual, Surface);
		return _compositor is DrawListCompositor d ? d.LastDraw(Surface) : UIDrawData.Empty;
	}

	/// <summary>The element tree annotated with <c>DesiredSize</c>/<c>LayoutRect</c> after a layout pass — the
	/// layout-test snapshot (PlanningUI.md §D8.1).</summary>
	public string DumpLayout()
	{
		var sb = new StringBuilder();
		if (Content is not null)
			DumpLayout(sb, Content, 0);
		return sb.ToString();
	}

	private static void DumpLayout(StringBuilder sb, UIElement el, int depth)
	{
		var c = CultureInfo.InvariantCulture;
		sb.Append(' ', depth * 2);
		sb.Append(c, $"{el.GetType().Name} desired={el.DesiredSize} rect={el.LayoutRect}\n");
		foreach (var child in el.VisualChildren)
			DumpLayout(sb, child, depth + 1);
	}
}
