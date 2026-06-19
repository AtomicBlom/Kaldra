using Loom.Core;
using Loom.UI.Controls;

namespace Loom.UI.Tests;

/// <summary>
/// MU0 Phase B layout coverage (PlanningUI.md §D2 / §D8): two-pass measure/arrange for StackPanel, Border insets,
/// alignment, Grid pixel/auto/star track resolution, and Canvas absolute placement — built as a tree, run through
/// <see cref="UIRoot.LayoutPass"/>, and asserted on <c>DesiredSize</c>/<c>LayoutRect</c> with no engine.
/// </summary>
public sealed class LayoutTests
{
	private static UIRoot Root(UIElement content, float w = 200f, float h = 200f)
	{
		var root = new UIRoot(new UiSurfaceId(0)) { Content = content };
		root.LayoutPass(new Size(w, h));
		return root;
	}

	[Test]
	public void StackPanel_vertical_sums_main_axis_and_maxes_cross_axis()
	{
		var b1 = new Border { Width = 100, Height = 20 };
		var b2 = new Border { Width = 50, Height = 30 };
		var sp = new StackPanel();
		sp.Children.Add(b1);
		sp.Children.Add(b2);
		Root(sp);

		sp.DesiredSize.ShouldBe(new Size(100, 50));
		b1.LayoutRect.ShouldBe(new Rect(0, 0, 100, 20));
		b2.LayoutRect.ShouldBe(new Rect(0, 20, 50, 30));
	}

	[Test]
	public void Border_adds_padding_and_border_around_its_child()
	{
		var inner = new Border { Width = 50, Height = 30 };
		var outer = new Border { Padding = new Thickness(10), BorderThickness = new Thickness(2), Child = inner };
		Root(outer);

		outer.DesiredSize.ShouldBe(new Size(50 + 24, 30 + 24)); // 12 inset per side
		inner.LayoutRect.ShouldBe(new Rect(12, 12, 50, 30));
	}

	[Test]
	public void Center_alignment_positions_within_the_slot()
	{
		var b = new Border { Width = 40, Height = 40, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
		Root(b);
		b.LayoutRect.ShouldBe(new Rect((200 - 40) / 2f, (200 - 40) / 2f, 40, 40));
	}

	[Test]
	public void Grid_resolves_pixel_then_star_for_the_leftover()
	{
		var grid = new Grid();
		grid.ColumnDefinitions.Add(GridLength.Pixels(100));
		grid.ColumnDefinitions.Add(GridLength.Star);
		var fixedCell = new Border { Width = 10, Height = 10 };
		var starCell = new Border();
		Grid.SetColumn(starCell, 1);
		grid.Children.Add(fixedCell);
		grid.Children.Add(starCell);
		Root(grid, 300, 100);

		// star column gets the 200 leftover, starting at x=100.
		starCell.LayoutRect.ShouldBe(new Rect(100, 0, 200, 100));
	}

	[Test]
	public void Grid_auto_track_sizes_to_its_child()
	{
		var grid = new Grid();
		grid.ColumnDefinitions.Add(GridLength.Auto);
		grid.ColumnDefinitions.Add(GridLength.Star);
		var autoCell = new Border { Width = 40, Height = 10 };
		var starCell = new Border();
		Grid.SetColumn(starCell, 1);
		grid.Children.Add(autoCell);
		grid.Children.Add(starCell);
		Root(grid, 300, 100);

		starCell.LayoutRect.ShouldBe(new Rect(40, 0, 260, 100)); // star = 300 - auto(40)
	}

	[Test]
	public void Canvas_places_children_at_their_attached_left_top()
	{
		var dot = new Border { Width = 8, Height = 8 };
		Canvas.SetLeft(dot, 30);
		Canvas.SetTop(dot, 15);
		var canvas = new Canvas();
		canvas.Children.Add(dot);
		Root(canvas);
		dot.LayoutRect.ShouldBe(new Rect(30, 15, 8, 8));
	}

	[Test]
	public void Changing_a_child_size_invalidates_measure_up_the_tree()
	{
		var child = new Border { Width = 10, Height = 10 };
		var sp = new StackPanel();
		sp.Children.Add(child);
		var root = Root(sp);

		sp.MeasureDirty.ShouldBeFalse(); // clean after the pass
		child.Width = 20;
		child.MeasureDirty.ShouldBeTrue();
		sp.MeasureDirty.ShouldBeTrue(); // propagated to the parent
	}
}
