using BinaryVibrance.Kaldra.Core.Graph;
using BinaryVibrance.Kaldra.Graph;

namespace BinaryVibrance.Kaldra.Tests;

/// <summary>Transient aliasing: partitioning non-overlapping lifetimes into shared-memory buckets.</summary>
public class AliasPlanTests
{
	private static AliasPlan Derive(RenderGraph graph) =>
		AliasPlan.Derive(LifetimePlan.Derive(GraphSchedule.Derive(graph)));

	[Test]
	public void Chain_folds_disjoint_transients_into_shared_buckets()
	{
		// a[0,1] b[1,2] c[2,3] d[3,4]: a dies before c, b dies before d -> two buckets {a,c}, {b,d}.
		var plan = Derive(RenderGraph.Define(g =>
		{
			var world = g.Domain("world", 320, 180);
			var screen = g.Backbuffer;
			var a = world.Color("a", Format.Rgba16F);
			var b = world.Color("b", Format.Rgba16F);
			var c = world.Color("c", Format.Rgba16F);
			var d = world.Color("d", Format.Rgba16F);
			g.Pass("p0").Writes(a);
			g.Pass("p1").Samples(a).Writes(b);
			g.Pass("p2").Samples(b).Writes(c);
			g.Pass("p3").Samples(c).Writes(d);
			g.Pass("p4").Samples(d).Writes(screen);
			g.Present(screen);
		}));

		plan.AliasableCount.ShouldBe(4);
		plan.Buckets.Length.ShouldBe(2); // peak overlap is 2

		// a and c share; b and d share; a and b do not. The expected sets are hoisted out of the assertions because
		// Shouldly takes a predicate as an expression tree: an array built *inside* one is constructed at runtime,
		// which NativeAOT cannot do and reports as IL3050 — a captured variable is just a closure field.
		string[] ac = ["a", "c"];
		string[] bd = ["b", "d"];
		var names = plan.Buckets.Select(bk => bk.Members.Select(m => m.Name).ToHashSet()).ToList();
		names.ShouldContain(s => s.SetEquals(ac));
		names.ShouldContain(s => s.SetEquals(bd));
	}

	[Test]
	public void Members_of_a_bucket_have_non_overlapping_lifetimes()
	{
		var plan = Derive(RenderGraph.Define(g =>
		{
			var world = g.Domain("world", 320, 180);
			var screen = g.Backbuffer;
			var a = world.Color("a", Format.Rgba16F);
			var b = world.Color("b", Format.Rgba16F);
			var c = world.Color("c", Format.Rgba16F);
			g.Pass("p0").Writes(a);
			g.Pass("p1").Samples(a).Writes(b);
			g.Pass("p2").Samples(b).Writes(c);
			g.Pass("p3").Samples(c).Writes(screen);
			g.Present(screen);
		}));

		foreach (var bucket in plan.Buckets)
			for (var i = 0; i < bucket.Members.Length; i++)
				for (var j = i + 1; j < bucket.Members.Length; j++)
				{
					var x = plan.Lifetimes.Of(bucket.Members[i])!.Value;
					var y = plan.Lifetimes.Of(bucket.Members[j])!.Value;
					x.Overlaps(y).ShouldBeFalse(); // never co-resident
				}
	}

	[Test]
	public void All_simultaneously_live_targets_get_their_own_bucket()
	{
		// The reference frame's g-buffer targets are all live into lighting (order 1), so none can alias.
		var plan = Derive(RenderGraph.Define(g =>
		{
			var world = g.Domain("world", 320, 180);
			var screen = g.Backbuffer;
			var albedo = world.Color("albedo", Format.Rgba8Srgb);
			var normal = world.Color("normal", Format.Rgb10A2);
			var material = world.Color("material", Format.Rgba8Unorm);
			var depth = world.Depth("depth", Format.Depth32F);
			var lit = world.Color("lit", Format.Rgba16F);

			g.Pass("gbuffer").Writes(albedo, normal, material).WritesDepth(depth);
			g.Pass("lighting").Samples(albedo, normal, material, depth).Writes(lit);
			g.Pass("transparent").Blends(lit).ReadsDepth(depth);
			g.Pass("upscale").Samples(lit).Writes(screen);
			g.Pass("hud").Writes(screen);
			g.Present(screen);
		}));

		// albedo, normal, material, depth, lit all touch order 1 -> 5 buckets, no folding.
		plan.AliasableCount.ShouldBe(5);
		plan.Buckets.Length.ShouldBe(5);
	}

	[Test]
	public void Backbuffer_is_excluded_from_aliasing()
	{
		var plan = Derive(RenderGraph.Define(g =>
		{
			var world = g.Domain("world", 320, 180);
			var screen = g.Backbuffer;
			var offscreen = world.Color("offscreen", Format.Rgba16F);
			g.Pass("scene").Writes(offscreen);
			g.Pass("present").Samples(offscreen).Writes(screen);
			g.Present(screen);
		}));

		var resources = plan.Lifetimes.Schedule.Graph.Resources;
		var screen = resources.Single(r => r.IsBackbuffer);
		var offscreen = resources.Single(r => r.Name == "offscreen");
		plan.BucketOf(screen).ShouldBeNull();     // swapchain-owned, not aliased
		plan.BucketOf(offscreen).ShouldNotBeNull();
	}

	[Test]
	public void Single_transient_yields_one_bucket()
	{
		var plan = Derive(RenderGraph.Define(g =>
		{
			var world = g.Domain("world", 320, 180);
			var screen = g.Backbuffer;
			var only = world.Color("only", Format.Rgba16F);
			g.Pass("scene").Writes(only);
			g.Pass("present").Samples(only).Writes(screen);
			g.Present(screen);
		}));

		plan.AliasableCount.ShouldBe(1);
		plan.Buckets.ShouldHaveSingleItem();
	}

	[Test]
	public void Readback_target_is_not_aliased_by_a_later_transient()
	{
		// a[0,1] would normally fold with c[2,2] (disjoint lifetimes). But `a` is read back to the host after
		// every pass, so its memory must survive to end of frame — the aliaser must NOT hand it to c, else the
		// readback copies c's stomped contents (the picking-flicker bug). Mirrors the real id/outline/bloom shape.
		var withReadback = Derive(RenderGraph.Define(g =>
		{
			var world = g.Domain("world", 320, 180);
			var a = world.Color("a", Format.R16F);
			var b = world.Color("b", Format.Rgba16F);
			var c = world.Color("c", Format.Rgba16F);
			g.Pass("p0").Writes(a);
			g.Pass("p1").Samples(a).Writes(b);
			g.Pass("p2").Samples(b).Writes(c);
			g.Readback(a);
		}));

		var res = withReadback.Lifetimes.Schedule.Graph.Resources;
		withReadback.BucketOf(res.Single(r => r.Name == "c")).ShouldNotBe(withReadback.BucketOf(res.Single(r => r.Name == "a")));

		// Pin the behaviour the fix changes: WITHOUT the readback, a and c DO fold into one shared bucket.
		var noReadback = Derive(RenderGraph.Define(g =>
		{
			var world = g.Domain("world", 320, 180);
			var a = world.Color("a", Format.R16F);
			var b = world.Color("b", Format.Rgba16F);
			var c = world.Color("c", Format.Rgba16F);
			g.Pass("p0").Writes(a);
			g.Pass("p1").Samples(a).Writes(b);
			g.Pass("p2").Samples(b).Writes(c);
		}));
		var bare = noReadback.Lifetimes.Schedule.Graph.Resources;
		noReadback.BucketOf(bare.Single(r => r.Name == "c")).ShouldBe(noReadback.BucketOf(bare.Single(r => r.Name == "a")));
	}
}
