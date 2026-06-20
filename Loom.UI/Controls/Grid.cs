using Loom.Core.Primitives;

namespace Loom.UI.Controls;

/// <summary>
/// A row/column grid (PlanningUI.md §D2.3): Pixel tracks are fixed, Auto tracks size to their largest child, and
/// Star tracks share the leftover space by weight after Pixel/Auto are subtracted. Children select a cell with the
/// attached <see cref="RowProperty"/>/<see cref="ColumnProperty"/> and may span tracks with
/// <see cref="RowSpanProperty"/>/<see cref="ColumnSpanProperty"/>. An empty definition list defaults to one Star
/// track. MU2 limitation: a spanning child does not drive Auto-track sizing (only single-cell children do).
/// </summary>
public sealed class Grid : Panel
{
	public static readonly DependencyProperty<Grid, int> RowProperty =
		DependencyProperty.RegisterAttached<Grid, int>("Row",
			new PropertyMetadata<Grid, int> { DefaultValue = 0, Invalidates = InvalidationFlags.Measure });

	public static readonly DependencyProperty<Grid, int> ColumnProperty =
		DependencyProperty.RegisterAttached<Grid, int>("Column",
			new PropertyMetadata<Grid, int> { DefaultValue = 0, Invalidates = InvalidationFlags.Measure });

	public static readonly DependencyProperty<Grid, int> RowSpanProperty =
		DependencyProperty.RegisterAttached<Grid, int>("RowSpan",
			new PropertyMetadata<Grid, int> { DefaultValue = 1, Invalidates = InvalidationFlags.Measure });

	public static readonly DependencyProperty<Grid, int> ColumnSpanProperty =
		DependencyProperty.RegisterAttached<Grid, int>("ColumnSpan",
			new PropertyMetadata<Grid, int> { DefaultValue = 1, Invalidates = InvalidationFlags.Measure });

	public static void SetRow(UIElement e, int v) => e.SetValue(RowProperty, v);
	public static int GetRow(UIElement e) => e.GetValue(RowProperty);
	public static void SetColumn(UIElement e, int v) => e.SetValue(ColumnProperty, v);
	public static int GetColumn(UIElement e) => e.GetValue(ColumnProperty);
	public static void SetRowSpan(UIElement e, int v) => e.SetValue(RowSpanProperty, v);
	public static int GetRowSpan(UIElement e) => e.GetValue(RowSpanProperty);
	public static void SetColumnSpan(UIElement e, int v) => e.SetValue(ColumnSpanProperty, v);
	public static int GetColumnSpan(UIElement e) => e.GetValue(ColumnSpanProperty);

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

		// Final measure of each child against its (resolved) cell — the sum of the tracks it spans.
		foreach (var child in Children.Items)
		{
			var ci = Math.Clamp(GetColumn(child), 0, _colSizes.Length - 1);
			var ri = Math.Clamp(GetRow(child), 0, _rowSizes.Length - 1);
			child.Measure(new Size(SpanSum(_colSizes, ci, GetColumnSpan(child)), SpanSum(_rowSizes, ri, GetRowSpan(child))));
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
			var w = SpanSum(_colSizes, ci, GetColumnSpan(child));
			var h = SpanSum(_rowSizes, ri, GetRowSpan(child));
			child.Arrange(new Rect(xs[ci], ys[ri], w, h));
		}
		return finalSize;
	}

	// Sum the sizes of the (clamped) run of tracks a child spans starting at <paramref name="start"/>.
	private static float SpanSum(float[] sizes, int start, int span)
	{
		var end = Math.Min(sizes.Length, start + Math.Max(1, span));
		var s = 0f;
		for (var i = start; i < end; i++)
			s += sizes[i];
		return s;
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

	// Max desired extent of the single-cell children in a track (measured unconstrained for Auto sizing). Spanning
	// children are excluded — MU2 does not distribute a span's desired size across its Auto tracks (documented).
	private float AutoExtent(int track, bool horizontal)
	{
		var max = 0f;
		foreach (var child in Children.Items)
		{
			var t = horizontal ? GetColumn(child) : GetRow(child);
			var span = Math.Max(1, horizontal ? GetColumnSpan(child) : GetRowSpan(child));
			if (t != track || span != 1)
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
