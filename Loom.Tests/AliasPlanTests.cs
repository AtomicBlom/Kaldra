using Loom.Core;
using Loom.Graph;

namespace Loom.Tests;

/// <summary>Transient aliasing: partitioning non-overlapping lifetimes into shared-memory buckets.</summary>
public class AliasPlanTests
{
	private static AliasPlan Derive(RenderGraph graph) =>
		AliasPlan.Derive(LifetimePlan.Derive(GraphSchedule.Derive(graph)));

	[Fact]
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

		Assert.Equal(4, plan.AliasableCount);
		Assert.Equal(2, plan.Buckets.Length); // peak overlap is 2

		// a and c share; b and d share; a and b do not.
		var names = plan.Buckets.Select(bk => bk.Members.Select(m => m.Name).ToHashSet()).ToList();
		Assert.Contains(names, s => s.SetEquals(["a", "c"]));
		Assert.Contains(names, s => s.SetEquals(["b", "d"]));
	}

	[Fact]
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
					Assert.False(x.Overlaps(y)); // never co-resident
				}
	}

	[Fact]
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
		Assert.Equal(5, plan.AliasableCount);
		Assert.Equal(5, plan.Buckets.Length);
	}

	[Fact]
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
		Assert.Null(plan.BucketOf(screen));     // swapchain-owned, not aliased
		Assert.NotNull(plan.BucketOf(offscreen));
	}

	[Fact]
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

		Assert.Equal(1, plan.AliasableCount);
		Assert.Single(plan.Buckets);
	}
}
