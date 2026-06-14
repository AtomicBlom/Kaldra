using Loom.Core;
using Loom.Graph;

namespace Loom.Tests;

/// <summary>Lifetime analysis: the execution-order spans the transient aliaser recycles memory across.</summary>
public class LifetimePlanTests
{
	private static LifetimePlan Derive(RenderGraph graph) => LifetimePlan.Derive(GraphSchedule.Derive(graph));

	private static ResourceLifetime Life(LifetimePlan plan, string name) =>
		plan.Lifetimes.Single(l => l.Resource.Name == name);

	// The §6 reference frame: gbuffer(0) lighting(1) transparent(2) upscale(3) hud(4).
	private static LifetimePlan ReferenceFrame() => Derive(RenderGraph.Define(g =>
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

	[Fact]
	public void Span_runs_from_first_touch_to_last_touch()
	{
		var plan = ReferenceFrame();

		// albedo: written by gbuffer (0), sampled by lighting (1).
		Assert.Equal((0, 1), (Life(plan, "albedo").FirstUse, Life(plan, "albedo").LastUse));
		// depth: gbuffer (0) writes, lighting (1) samples, transparent (2) reads -> [0,2].
		Assert.Equal((0, 2), (Life(plan, "depth").FirstUse, Life(plan, "depth").LastUse));
		// lit: lighting (1) writes, transparent (2) blends, upscale (3) samples -> [1,3].
		Assert.Equal((1, 3), (Life(plan, "lit").FirstUse, Life(plan, "lit").LastUse));
		// backbuffer: upscale (3) and hud (4) -> [3,4].
		Assert.Equal((3, 4), (Life(plan, "<backbuffer>").FirstUse, Life(plan, "<backbuffer>").LastUse));
	}

	[Fact]
	public void Backbuffer_is_not_aliasable()
	{
		var plan = ReferenceFrame();
		Assert.False(Life(plan, "<backbuffer>").Aliasable);
		Assert.True(Life(plan, "albedo").Aliasable);
		Assert.True(Life(plan, "depth").Aliasable);
	}

	[Fact]
	public void Overlapping_lifetimes_cannot_alias()
	{
		var plan = ReferenceFrame();
		// albedo [0,1] and lit [1,3] are both live at order 1.
		Assert.True(Life(plan, "albedo").Overlaps(Life(plan, "lit")));
		Assert.False(Life(plan, "albedo").CanAlias(Life(plan, "lit")));
	}

	[Fact]
	public void Disjoint_transient_lifetimes_can_alias()
	{
		// a [0,1], b [1,2], c [2,2]: a is dead before c is born, so c may reuse a's memory.
		var plan = Derive(RenderGraph.Define(g =>
		{
			var world = g.Domain("world", 320, 180);
			var a = world.Color("a", Format.Rgba16F);
			var b = world.Color("b", Format.Rgba16F);
			var c = world.Color("c", Format.Rgba16F);
			g.Pass("p0").Writes(a);
			g.Pass("p1").Samples(a).Writes(b);
			g.Pass("p2").Samples(b).Writes(c);
		}));

		Assert.True(Life(plan, "a").CanAlias(Life(plan, "c")));   // [0,1] vs [2,2] disjoint
		Assert.False(Life(plan, "a").CanAlias(Life(plan, "b")));  // [0,1] vs [1,2] share order 1
		Assert.False(Life(plan, "b").CanAlias(Life(plan, "c")));  // [1,2] vs [2,2] share order 2
	}

	[Fact]
	public void Single_pass_resource_has_a_point_lifetime()
	{
		var plan = Derive(RenderGraph.Define(g =>
		{
			var world = g.Domain("world", 320, 180);
			var only = world.Color("only", Format.Rgba16F);
			g.Pass("p").Writes(only);
		}));

		var life = Life(plan, "only");
		Assert.Equal(life.FirstUse, life.LastUse);
	}

	[Fact]
	public void Declared_but_unused_resource_gets_no_lifetime()
	{
		ResourceHandle? unused = null;
		var plan = Derive(RenderGraph.Define(g =>
		{
			var world = g.Domain("world", 320, 180);
			var used = world.Color("used", Format.Rgba16F);
			unused = world.Color("unused", Format.Rgba16F); // declared, never touched by a pass
			g.Pass("p").Writes(used);
		}));

		Assert.DoesNotContain(plan.Lifetimes, l => l.Resource.Name == "unused");
		Assert.Null(plan.Of(unused!));
	}

	[Fact]
	public void Persistent_resource_is_not_aliasable()
	{
		var plan = Derive(RenderGraph.Define(g =>
		{
			var world = g.Domain("world", 320, 180);
			var history = g.Persistent("history", Format.Rgba16F);
			var lit = world.Color("lit", Format.Rgba16F);
			g.Pass("read").Samples(history).Writes(lit);   // persistent legitimately read before write
			g.Pass("write").Writes(history).Samples(lit);
		}));

		Assert.False(Life(plan, "history").Aliasable);
	}

	[Fact]
	public void Readback_target_lifetime_extends_to_end_of_frame()
	{
		// `a` is read back to the host (g.Readback). That copy runs AFTER every pass, so `a` must stay live
		// to end of frame; otherwise the aliaser recycles its memory and the readback samples whatever folded
		// into it (the picking-flicker bug). Mirrors the real id -> outline -> bloom shape, where object_id
		// would otherwise die at its last sample and get stomped.
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

		// 3 passes -> orders 0..2; the readback pushes `a`'s last use to 3, one past the final pass.
		Assert.Equal(3, withReadback.Schedule.Passes.Length);
		Assert.Equal((0, 3), (Life(withReadback, "a").FirstUse, Life(withReadback, "a").LastUse));

		// Guard against over-extension: the SAME graph without the readback leaves `a` at its natural [0,1].
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
		Assert.Equal((0, 1), (Life(noReadback, "a").FirstUse, Life(noReadback, "a").LastUse));
	}
}
