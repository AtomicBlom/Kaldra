using BinaryVibrance.Kaldra.Graph;

namespace BinaryVibrance.Kaldra.Tests;

/// <summary>
/// The nested GPU debug-label scopes derived from dotted pass names. RenderDoc and PIX build their event tree from
/// begin/end <em>nesting depth</em>, not from label text, so this derivation is the only thing standing between a
/// readable capture and a corrupted one — and it fails quietly: an off-by-one in the close count doesn't throw, it
/// just reparents (or unbalances) everything after it in a tool nobody runs in CI. Hence the balance invariant below.
/// </summary>
public class DebugLabelScopesTests
{
	[Test]
	public void An_undotted_name_stays_top_level_with_no_group_wrapper()
	{
		var plan = DebugLabelScopes.Derive(["gbuffer", "lighting"]);

		plan.Passes.Length.ShouldBe(2);
		plan.Passes[0].ShouldSatisfyAllConditions(
			() => plan.Passes[0].Leaf.ShouldBe("gbuffer"),
			() => plan.Passes[0].Opens.ShouldBeEmpty(),
			() => plan.Passes[0].Closes.ShouldBe(0));
		plan.Passes[1].Leaf.ShouldBe("lighting");
		plan.TrailingCloses.ShouldBe(0);
	}

	[Test]
	public void A_dotted_run_opens_its_group_once_and_closes_it_when_the_prefix_ends()
	{
		var plan = DebugLabelScopes.Derive(["gbuffer", "bloom.down1", "bloom.down2", "bloom.up0", "hud"]);

		plan.Passes[1].Opens.ShouldBe(["bloom"]);      // the group opens on the first member…
		plan.Passes[1].Leaf.ShouldBe("down1");
		plan.Passes[2].Opens.ShouldBeEmpty();          // …stays open across the rest of the run…
		plan.Passes[2].Closes.ShouldBe(0);
		plan.Passes[3].Opens.ShouldBeEmpty();
		plan.Passes[4].Closes.ShouldBe(1);             // …and closes when a pass leaves the prefix
		plan.Passes[4].Leaf.ShouldBe("hud");
		plan.TrailingCloses.ShouldBe(0);
	}

	[Test]
	public void Groups_nest_and_only_the_diverging_suffix_is_reopened()
	{
		var plan = DebugLabelScopes.Derive(["a.b.c", "a.b.d", "a.e.f", "top"]);

		plan.Passes[0].Opens.ShouldBe(["a", "b"]);
		plan.Passes[0].Leaf.ShouldBe("c");
		plan.Passes[1].Opens.ShouldBeEmpty();          // same group path — nothing to reopen
		plan.Passes[2].Closes.ShouldBe(1);             // "b" ends…
		plan.Passes[2].Opens.ShouldBe(["e"]);          // …"a" stays open, "e" opens under it
		plan.Passes[3].Closes.ShouldBe(2);             // "e" and "a" both end
		plan.TrailingCloses.ShouldBe(0);
	}

	[Test]
	public void Groups_still_open_after_the_last_pass_are_reported_as_trailing_closes()
	{
		var plan = DebugLabelScopes.Derive(["setup", "bloom.down1", "bloom.up0"]);

		plan.TrailingCloses.ShouldBe(1); // the backend closes "bloom" before the frame ends
	}

	[Test]
	public void Every_group_that_opens_is_closed_exactly_once()
	{
		// The invariant a corrupted event tree violates, checked over a run that exercises deepening, staying,
		// diverging, and unwinding — the four transitions the derivation has to get right.
		string[] names =
		[
			"clear",
			"shadow.cascade0", "shadow.cascade1",
			"gbuffer.opaque", "gbuffer.decals.project", "gbuffer.decals.blend",
			"lighting",
			"post.bloom.down0", "post.bloom.down1", "post.bloom.up0", "post.tonemap",
			"ui.panels.left", "ui.panels.right",
		];
		var plan = DebugLabelScopes.Derive(names);

		var opens = plan.Passes.Sum(p => p.Opens.Length);
		var closes = plan.Passes.Sum(p => p.Closes) + plan.TrailingCloses;
		closes.ShouldBe(opens, "every group scope opened must be closed exactly once, or the capture's event tree is corrupt.");

		// And the running depth must never go negative — an early close would pop a scope that isn't ours.
		var depth = 0;
		foreach (var pass in plan.Passes)
		{
			depth -= pass.Closes;
			depth.ShouldBeGreaterThanOrEqualTo(0, $"pass '{pass.Leaf}' closes more group scopes than are open.");
			depth += pass.Opens.Length;
		}
		depth.ShouldBe(plan.TrailingCloses);
	}

	[Test]
	public void No_passes_derives_an_empty_balanced_plan()
	{
		var plan = DebugLabelScopes.Derive([]);

		plan.Passes.ShouldBeEmpty();
		plan.TrailingCloses.ShouldBe(0);
	}
}
