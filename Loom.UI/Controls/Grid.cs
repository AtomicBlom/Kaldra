using Loom.Core.Primitives;

namespace Loom.UI.Controls;

/// <summary>
/// A row/column grid (PlanningUI.md §D2.3): Pixel tracks are fixed, Auto tracks size to their largest child, and
/// Star tracks share the leftover space by weight after Pixel/Auto are subtracted. Children select a cell with the
/// attached <see cref="RowProperty"/>/<see cref="ColumnProperty"/>. Phase B keeps it single-cell (no Row/Column
/// span — that lands with the full Grid in MU2); an empty definition list defaults to one Star track.
/// </summary>
public sealed class Grid : Panel
{
	public static readonly DependencyProperty<Grid, int> RowProperty =
		DependencyProperty.RegisterAttached<Grid, int>("Row",
			new PropertyMetadata<Grid, int> { DefaultValue = 0, Invalidates = InvalidationFlags.Measure });

	public static readonly DependencyProperty<Grid, int> ColumnProperty =
		DependencyProperty.RegisterAttached<Grid, int>("Column",
			new PropertyMetadata<Grid, int> { DefaultValue = 0, Invalidates = InvalidationFlags.Measure });

	public static void SetRow(UIElement e, int v) => e.SetValue(RowProperty, v);
	public static int GetRow(UIElement e) => e.GetValue(RowProperty);
	public static void SetColumn(UIElement e, int v) => e.SetValue(ColumnProperty, v);
	public static int GetColumn(UIElement e) => e.GetValue(ColumnProperty);

	/// <summary>Column track lengths (empty ⇒ a single Star column).</summary>
	public IList<GridLength> ColumnDefinitions { get; } = [];

	/// <summary>Row track lengths (empty ⇒ a single Star row).</summary>
	public IList<GridLength> RowDefinitions { get; } = [];

	private float[] _colSizes = [];
	private float[] _rowSizes = [];

	protected override Size MeasureOverride(Size available)
	{
		var cols = ColumnDefinitions.Count > 0 ? [.. ColumnDefinitions] : new[] { GridLength.Star };
		var rows = RowDefinitions.Count > 0 ? [.. RowDefinitions] : new[] { GridLength.Star };

		_colSizes = ResolveTracks(cols, available.Width, horizontal: true);
		_rowSizes = ResolveTracks(rows, available.Height, horizontal: false);

		// Final measure of each child against its (resolved) cell.
		foreach (var child in Children.Items)
		{
			var ci = Math.Clamp(GetColumn(child), 0, _colSizes.Length - 1);
			var ri = Math.Clamp(GetRow(child), 0, _rowSizes.Length - 1);
			child.Measure(new Size(_colSizes[ci], _rowSizes[ri]));
		}

		return new Size(Sum(_colSizes), Sum(_rowSizes));
	}

	protected override Size ArrangeOverride(Size finalSize)
	{
		var xs = Offsets(_colSizes);
		var ys = Offsets(_rowSizes);
		foreach (var child in Children.Items)
		{
			var ci = Math.Clamp(GetColumn(child), 0, _colSizes.Length - 1);
			var ri = Math.Clamp(GetRow(child), 0, _rowSizes.Length - 1);
			child.Arrange(new Rect(xs[ci], ys[ri], _colSizes[ci], _rowSizes[ri]));
		}
		return finalSize;
	}

	// Resolve one axis: Pixel fixed, Auto = max child desired in that track, Star = proportional share of leftover.
	private float[] ResolveTracks(GridLength[] defs, float available, bool horizontal)
	{
		var sizes = new float[defs.Length];
		float used = 0f, starWeight = 0f;
		var hasFiniteSpace = !float.IsPositiveInfinity(available);

		for (var i = 0; i < defs.Length; i++)
		{
			var d = defs[i];
			if (d.IsPixel) { sizes[i] = d.Value; used += d.Value; }
			else if (d.IsAuto) { sizes[i] = AutoExtent(i, horizontal); used += sizes[i]; }
			else starWeight += d.Value;
		}

		var leftover = hasFiniteSpace ? MathF.Max(0f, available - used) : 0f;
		if (starWeight > 0f)
			for (var i = 0; i < defs.Length; i++)
				if (defs[i].IsStar)
					sizes[i] = leftover * (defs[i].Value / starWeight);
		return sizes;
	}

	// Max desired extent of the children in a track (measured unconstrained for Auto sizing).
	private float AutoExtent(int track, bool horizontal)
	{
		float max = 0f;
		foreach (var child in Children.Items)
		{
			var t = horizontal ? GetColumn(child) : GetRow(child);
			if (t != track)
				continue;
			child.Measure(Size.Infinite);
			max = MathF.Max(max, horizontal ? child.DesiredSize.Width : child.DesiredSize.Height);
		}
		return max;
	}

	private static float Sum(float[] a) { float s = 0f; foreach (var v in a) s += v; return s; }

	private static float[] Offsets(float[] sizes)
	{
		var o = new float[sizes.Length];
		float acc = 0f;
		for (var i = 0; i < sizes.Length; i++) { o[i] = acc; acc += sizes[i]; }
		return o;
	}
}
