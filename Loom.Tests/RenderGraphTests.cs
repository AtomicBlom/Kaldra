using Loom.Core;

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

	[Fact]
	public void Hash_is_deterministic_across_identical_builds() =>
		Assert.Equal(Baseline().Hash(), Baseline().Hash());

	[Fact]
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
		Assert.Equal(a.Hash(), b.Hash());
		Assert.NotEqual(withEmpty.Hash(), a.Hash());
	}

	[Fact]
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

		Assert.Equal(ab.Hash(), ba.Hash());
	}

	[Fact]
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

		Assert.NotEqual(xy.Hash(), yx.Hash());
	}

	[Fact]
	public void Hash_is_sensitive_to_format()
	{
		var srgb = RenderGraph.Define(g => g.Domain("world", 320, 180).Color("a", Format.Rgba8Srgb));
		var hdr = RenderGraph.Define(g => g.Domain("world", 320, 180).Color("a", Format.Rgba16F));
		Assert.NotEqual(srgb.Hash(), hdr.Hash());
	}

	[Fact]
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
		Assert.NotEqual(writes.Hash(), blends.Hash());
	}

	[Fact]
	public void Present_target_is_recorded()
	{
		var graph = Baseline();
		Assert.NotNull(graph.PresentTarget);
		Assert.True(graph.PresentTarget!.IsBackbuffer);
	}

	[Fact]
	public void Graph_without_present_has_null_target()
	{
		var graph = RenderGraph.Define(g => g.Domain("world", 320, 180).Color("a", Format.Rgba8Srgb));
		Assert.Null(graph.PresentTarget);
	}

	[Fact]
	public void Dump_mentions_domains_resources_passes_and_present()
	{
		var dump = Baseline().Dump();
		Assert.Contains("world", dump);
		Assert.Contains("albedo", dump);
		Assert.Contains("lighting", dump);
		Assert.Contains("Present: <backbuffer>", dump);
	}

	/// <summary>
	/// The §6 reference-frame shape (minus the g.Bloom sub-graph helper, which is M5): deferred geometry
	/// → lighting → forward transparency → nearest upscale → HUD → present. Proves the vocabulary composes.
	/// </summary>
	[Fact]
	public void Reference_frame_shape_composes()
	{
		ResourceHandle? lit = null, depth = null;
		var graph = RenderGraph.Define(g =>
		{
			var world = g.Domain("world", 320, 180);
			var screen = g.Backbuffer;

			var albedo = world.Color("albedo", Format.Rgba8Srgb);
			var normal = world.Color("normal", Format.Rgb10A2);
			var material = world.Color("material", Format.Rgba8Unorm);
			depth = world.Depth("depth", Format.Depth32F);
			lit = world.Color("lit", Format.Rgba16F);

			g.Pass("gbuffer").Writes(albedo, normal, material).WritesDepth(depth);
			g.Pass("lighting").Samples(albedo, normal, material, depth).Writes(lit);
			g.Pass("transparent").Blends(lit).ReadsDepth(depth);
			g.Pass("upscale").Samples(lit).Writes(screen);
			g.Pass("hud").Writes(screen);
			g.Present(screen);
		});

		Assert.Equal(["gbuffer", "lighting", "transparent", "upscale", "hud"], graph.Passes.Select(p => p.Name));

		// Two domains: the low-res world plus the native backbuffer.
		Assert.Equal(2, graph.Domains.Length);

		// lit is produced by lighting and accumulated onto by transparent.
		Assert.Contains(graph.Passes.Single(p => p.Name == "lighting").ResourcesWith(AccessKind.Write), r => r == lit);
		Assert.Contains(graph.Passes.Single(p => p.Name == "transparent").ResourcesWith(AccessKind.Blend), r => r == lit);

		// depth is written once and only read by the transparent pass.
		Assert.Contains(graph.Passes.Single(p => p.Name == "gbuffer").ResourcesWith(AccessKind.WriteDepth), r => r == depth);
		Assert.Contains(graph.Passes.Single(p => p.Name == "transparent").ResourcesWith(AccessKind.ReadDepth), r => r == depth);

		// Both backbuffer writers target the one swapchain resource; present points at it.
		Assert.True(graph.PresentTarget!.IsBackbuffer);
	}
}
