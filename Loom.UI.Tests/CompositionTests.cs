using Loom.Core;
using Loom.UI.Controls;

namespace Loom.UI.Tests;

/// <summary>
/// MU0 Phase B composition coverage (PlanningUI.md §D2.5 / §D8): the element tree drives a <see cref="Visual"/>
/// tree, and the default <see cref="DrawListCompositor"/> flattens it to neutral <see cref="UiDrawData"/>. Both
/// stages are asserted on CPU via their <c>Dump()</c> forms (the two pure test seams) — no backend.
/// </summary>
public sealed class CompositionTests
{
	private static UIRoot Root(UIElement content, float w = 200f, float h = 200f)
	{
		var root = new UIRoot(new UiSurfaceId(0)) { Content = content };
		root.LayoutPass(new Size(w, h));
		return root;
	}

	[Test]
	public void A_border_background_composites_to_one_fill_command()
	{
		var border = new Border { Background = HdrColor.FromRgb(200, 0, 0), Width = 100, Height = 50 };
		var draw = Root(border, 100, 50).Render();

		draw.Commands.Length.ShouldBe(1);
		var dump = draw.Dump();
		dump.ShouldContain("Commands (1)");
		dump.ShouldContain("Fill tex=0");
		dump.ShouldContain("pos=(100,50)"); // bottom-right corner of the 100x50 fill
	}

	[Test]
	public void The_element_tree_drives_a_matching_visual_tree()
	{
		var border = new Border { Background = HdrColor.FromRgb(0, 128, 255), Width = 64, Height = 32 };
		var dump = Root(border, 64, 32).Visual.Dump();

		dump.ShouldContain("RootVisual");
		dump.ShouldContain("SpriteVisual");
		dump.ShouldContain("rect=64x32");
		dump.ShouldContain("fill=");
	}

	[Test]
	public void Adjacent_fills_with_the_same_clip_batch_into_one_draw()
	{
		var sp = new StackPanel { Background = HdrColor.FromRgb(20, 20, 20) };
		sp.Children.Add(new Border { Background = HdrColor.FromRgb(255, 0, 0), Width = 80, Height = 20 });
		sp.Children.Add(new Border { Background = HdrColor.FromRgb(0, 255, 0), Width = 80, Height = 20 });
		var draw = Root(sp).Render();

		// Panel background + two child backgrounds = three quads, but one batched indexed draw (state-run batching).
		draw.Commands.Length.ShouldBe(1);
		draw.Vertices.Length.ShouldBe(12);
		draw.Indices.Length.ShouldBe(18);
	}

	[Test]
	public void Render_revision_advances_each_composite_for_no_op_detection()
	{
		var root = Root(new Border { Background = HdrColor.FromRgb(1, 2, 3), Width = 10, Height = 10 }, 10, 10);
		var r1 = root.Render().Revision;
		var r2 = root.Render().Revision;
		r2.ShouldBeGreaterThan(r1);
	}
}
