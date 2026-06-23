using Loom.Core.Graph;

namespace Loom.Tests;

/// <summary>The immutable graph: hashing, dump, present, and the reference frame composing end to end.</summary>
public class RenderGraphTests
{
	// A small but representative graph used as the baseline for hash sensitivity tests.
	private static RenderGraph Baseline() => RenderGraph.Define(g =>
	{
		var world = g.Domain("world", 320, 180);
		var albedo = world.Color("albedo", Format.Rgba8Srgb);
		var lit = world.Color("lit", Format.Rgba16F);
		g.Pass("gbuffer").Writes(albedo);
		g.Pass("lighting").Samples(albedo).Writes(lit);
		g.Present(g.Backbuffer);
	});

	[Test]
	public void Hash_is_deterministic_across_identical_builds() =>
		Baseline().Hash().ShouldBe(Baseline().Hash());

	[Test]
	public void Hash_ignores_record_callbacks()
	{
		var withEmpty = RenderGraph.Define(g => g.Domain("world", 320, 180).Color("a", Format.Rgba8Srgb));
		var a = RenderGraph.Define(g =>
		{
			var c = g.Domain("world", 320, 180).Color("a", Format.Rgba8Srgb);
			g.Pass("p").Writes(c).Draw(_ => { });
		});
		var b = RenderGraph.Define(g =>
		{
			var c = g.Domain("world", 320, 180).Color("a", Format.Rgba8Srgb);
			g.Pass("p").Writes(c).Draw(ctx => { _ = ctx.DeltaTime; });
		});

		// Different delegate bodies, identical structure -> identical hash. (withEmpty differs: no pass.)
		b.Hash().ShouldBe(a.Hash());
		a.Hash().ShouldNotBe(withEmpty.Hash());
	}

	[Test]
	public void Hash_is_insensitive_to_access_order_within_a_pass()
	{
		var ab = RenderGraph.Define(g =>
		{
			var world = g.Domain("world", 320, 180);
			var a = world.Color("a", Format.Rgba8Srgb);
			var b = world.Color("b", Format.Rgba8Srgb);
			g.Pass("p").Writes(a, b);
		});
		var ba = RenderGraph.Define(g =>
		{
			var world = g.Domain("world", 320, 180);
			var a = world.Color("a", Format.Rgba8Srgb);
			var b = world.Color("b", Format.Rgba8Srgb);
			g.Pass("p").Writes(b, a);
		});

		ba.Hash().ShouldBe(ab.Hash());
	}

	[Test]
	public void Hash_is_sensitive_to_pass_order()
	{
		// Program order is a real derivation input, so swapping passes must change the hash.
		var xy = RenderGraph.Define(g =>
		{
			var world = g.Domain("world", 320, 180);
			var x = world.Color("x", Format.Rgba8Srgb);
			var y = world.Color("y", Format.Rgba8Srgb);
			g.Pass("px").Writes(x);
			g.Pass("py").Writes(y);
		});
		var yx = RenderGraph.Define(g =>
		{
			var world = g.Domain("world", 320, 180);
			var x = world.Color("x", Format.Rgba8Srgb);
			var y = world.Color("y", Format.Rgba8Srgb);
			g.Pass("py").Writes(y);
			g.Pass("px").Writes(x);
		});

		yx.Hash().ShouldNotBe(xy.Hash());
	}

	[Test]
	public void Hash_is_sensitive_to_format()
	{
		var srgb = RenderGraph.Define(g => g.Domain("world", 320, 180).Color("a", Format.Rgba8Srgb));
		var hdr = RenderGraph.Define(g => g.Domain("world", 320, 180).Color("a", Format.Rgba16F));
		hdr.Hash().ShouldNotBe(srgb.Hash());
	}

	[Test]
	public void Hash_is_sensitive_to_write_vs_blend()
	{
		var writes = RenderGraph.Define(g =>
		{
			var lit = g.Domain("world", 320, 180).Color("lit", Format.Rgba16F);
			g.Pass("p").Writes(lit);
		});
		var blends = RenderGraph.Define(g =>
		{
			var lit = g.Domain("world", 320, 180).Color("lit", Format.Rgba16F);
			g.Pass("p").Blends(lit);
		});
		blends.Hash().ShouldNotBe(writes.Hash());
	}

	[Test]
	public void Present_target_is_recorded()
	{
		var graph = Baseline();
		graph.PresentTarget.ShouldNotBeNull();
		graph.PresentTarget!.IsBackbuffer.ShouldBeTrue();
	}

	[Test]
	public void Graph_without_present_has_null_target()
	{
		var graph = RenderGraph.Define(g => g.Domain("world", 320, 180).Color("a", Format.Rgba8Srgb));
		graph.PresentTarget.ShouldBeNull();
	}

	[Test]
	public void Dump_mentions_domains_resources_passes_and_present()
	{
		var dump = Baseline().Dump();
		dump.ShouldContain("world");
		dump.ShouldContain("albedo");
		dump.ShouldContain("lighting");
		dump.ShouldContain("Present: <backbuffer>");
	}

	/// <summary>
	/// Reference-frame shape: deferred geometry
	/// → lighting → forward transparency → nearest upscale → HUD → present. Proves the vocabulary composes.
	/// </summary>
	[Test]
	public void Reference_frame_shape_composes()
	{
		ColorHandle? lit = null;
		DepthHandle? depth = null;
		var graph = RenderGraph.Define(g =>
		{
			var world = g.Domain("world", 320, 180);
			var screen = g.Backbuffer;

			var albedo = world.Color("albedo", Format.Rgba8Srgb);
			var normal = world.Color("normal", Format.Rgb10A2);
			var material = world.Color("material", Format.Rgba8Unorm);
			var d = world.Depth("depth", Format.Depth32F);
			var l = world.Color("lit", Format.Rgba16F);
			depth = d;
			lit = l;

			g.Pass("gbuffer").Writes(albedo, normal, material).WritesDepth(d);
			g.Pass("lighting").Samples(albedo, normal, material, d).Writes(l);
			g.Pass("transparent").Blends(l).ReadsDepth(d);
			g.Pass("upscale").Samples(l).Writes(screen);
			g.Pass("hud").Writes(screen);
			g.Present(screen);
		});

		graph.Passes.Select(p => p.Name).ShouldBe(new[] { "gbuffer", "lighting", "transparent", "upscale", "hud" });

		// Two domains: the low-res world plus the native backbuffer.
		graph.Domains.Length.ShouldBe(2);

		// lit is produced by lighting and accumulated onto by transparent.
		graph.Passes.Single(p => p.Name == "lighting").ResourcesWith(AccessKind.Write).ShouldContain(r => r == lit);
		graph.Passes.Single(p => p.Name == "transparent").ResourcesWith(AccessKind.Blend).ShouldContain(r => r == lit);

		// depth is written once and only read by the transparent pass.
		graph.Passes.Single(p => p.Name == "gbuffer").ResourcesWith(AccessKind.WriteDepth).ShouldContain(r => r == depth);
		graph.Passes.Single(p => p.Name == "transparent").ResourcesWith(AccessKind.ReadDepth).ShouldContain(r => r == depth);

		// Both backbuffer writers target the one swapchain resource; present points at it.
		graph.PresentTarget!.IsBackbuffer.ShouldBeTrue();
	}
}
