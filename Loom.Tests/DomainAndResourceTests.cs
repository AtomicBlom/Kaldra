using Loom.Core;

namespace Loom.Tests;

/// <summary>Resource declaration, domains, handles — the nouns of the describe layer.</summary>
public class DomainAndResourceTests
{
	[Fact]
	public void Domain_stores_its_resolution()
	{
		RenderGraph.Define(g =>
		{
			var world = g.Domain("world", 320, 180);
			Assert.Equal("world", world.Name);
			Assert.Equal(320, world.Width);
			Assert.Equal(180, world.Height);
			Assert.False(world.IsBackbuffer);
		});
	}

	[Theory]
	[InlineData(0, 180)]
	[InlineData(320, 0)]
	[InlineData(-1, 180)]
	public void Domain_rejects_nonpositive_dimensions(int width, int height) =>
		Assert.Throws<ArgumentOutOfRangeException>(() =>
			RenderGraph.Define(g => g.Domain("bad", width, height)));

	[Fact]
	public void Screen_is_the_backbuffer_domain()
	{
		RenderGraph.Define(g =>
		{
			var screen = g.Screen;
			Assert.True(screen.IsBackbuffer);
			Assert.Same(g.Backbuffer.Domain, screen); // the same native-resolution domain
		});
	}

	[Fact]
	public void Relative_domain_records_its_scale_and_is_not_fixed()
	{
		RenderGraph.Define(g =>
		{
			var half = g.Domain("half", scale: 0.5f);
			Assert.Equal(0.5f, half.RelativeScale);
			Assert.False(half.IsBackbuffer);
			Assert.Equal(0, half.Width); // pixel size is backend-resolved against the swapchain
			Assert.Equal(0, half.Height);
		});
	}

	[Theory]
	[InlineData(0f)]
	[InlineData(-0.5f)]
	public void Relative_domain_rejects_nonpositive_scale(float scale) =>
		Assert.Throws<ArgumentOutOfRangeException>(() =>
			RenderGraph.Define(g => g.Domain("bad", scale)));

	[Fact]
	public void Relative_scale_distinguishes_the_graph_hash()
	{
		var a = RenderGraph.Define(g => { g.Domain("d", scale: 0.5f); });
		var b = RenderGraph.Define(g => { g.Domain("d", scale: 0.25f); });
		Assert.NotEqual(a.Hash(), b.Hash());
	}

	[Fact]
	public void Domain_records_its_msaa_sample_count()
	{
		RenderGraph.Define(g =>
		{
			var scene = g.Domain("scene", 1280, 720, samples: 4);
			Assert.Equal(4, scene.Samples);
			Assert.Equal(1, g.Domain("plain", 320, 180).Samples); // default is single-sample
		});
	}

	[Theory]
	[InlineData(0)]
	[InlineData(3)]
	[InlineData(6)]
	public void Domain_rejects_non_power_of_two_sample_count(int samples) =>
		Assert.Throws<ArgumentOutOfRangeException>(() =>
			RenderGraph.Define(g => g.Domain("bad", 320, 180, samples)));

	[Fact]
	public void Sample_count_distinguishes_the_graph_hash()
	{
		var a = RenderGraph.Define(g => { g.Domain("d", 320, 180, samples: 1); });
		var b = RenderGraph.Define(g => { g.Domain("d", 320, 180, samples: 4); });
		Assert.NotEqual(a.Hash(), b.Hash());
	}

	[Fact]
	public void Duplicate_domain_name_throws() =>
		Assert.Throws<InvalidOperationException>(() =>
			RenderGraph.Define(g =>
			{
				g.Domain("world", 320, 180);
				g.Domain("world", 640, 360);
			}));

	[Fact]
	public void Resources_get_sequential_ids_in_declaration_order()
	{
		var graph = RenderGraph.Define(g =>
		{
			var world = g.Domain("world", 320, 180);
			world.Color("albedo", Format.Rgba8Srgb);
			world.Color("normal", Format.Rgb10A2);
			world.Depth("depth", Format.Depth32F);
		});

		Assert.Equal(3, graph.Resources.Length);
		Assert.Equal([0, 1, 2], graph.Resources.Select(r => r.Id));
		Assert.Equal(["albedo", "normal", "depth"], graph.Resources.Select(r => r.Name));
	}

	[Fact]
	public void Color_and_depth_carry_their_kind_and_format()
	{
		ResourceHandle? albedo = null, depth = null;
		RenderGraph.Define(g =>
		{
			var world = g.Domain("world", 320, 180);
			albedo = world.Color("albedo", Format.Rgba8Srgb);
			depth = world.Depth("depth", Format.Depth32F);
		});

		Assert.Equal(ResourceKind.Color, albedo!.Kind);
		Assert.Equal(Format.Rgba8Srgb, albedo.Format);
		Assert.Equal(ResourceKind.Depth, depth!.Kind);
		Assert.Equal(Format.Depth32F, depth.Format);
	}

	[Fact]
	public void Handle_exposes_its_parent_domain()
	{
		Domain? world = null;
		ResourceHandle? albedo = null;
		RenderGraph.Define(g =>
		{
			world = g.Domain("world", 320, 180);
			albedo = world.Color("albedo", Format.Rgba8Srgb);
		});

		Assert.Same(world, albedo!.Domain);
	}

	[Fact]
	public void Scaled_color_resolves_against_its_domain()
	{
		ResourceHandle? half = null;
		RenderGraph.Define(g =>
		{
			var world = g.Domain("world", 320, 180);
			half = world.Color("bloom_mip1", Format.Rgba16F, scale: 0.5f);
		});

		Assert.Equal(0.5f, half!.Scale);
		Assert.Equal((160, 90), half.Resolve());
	}

	[Fact]
	public void Duplicate_resource_name_throws() =>
		Assert.Throws<InvalidOperationException>(() =>
			RenderGraph.Define(g =>
			{
				var world = g.Domain("world", 320, 180);
				world.Color("dup", Format.Rgba8Srgb);
				world.Color("dup", Format.Rgba16F);
			}));

	[Fact]
	public void Backbuffer_is_a_single_idempotent_handle()
	{
		var graph = RenderGraph.Define(g =>
		{
			var a = g.Backbuffer;
			var b = g.Backbuffer;
			Assert.Same(a, b);
		});

		// One backbuffer resource, one backbuffer domain — no duplicates from repeated access.
		Assert.Single(graph.Resources, r => r.IsBackbuffer);
		Assert.Single(graph.Domains, d => d.IsBackbuffer);
	}

	[Fact]
	public void Backbuffer_handle_is_backbuffer_kind_in_native_domain()
	{
		ResourceHandle? bb = null;
		RenderGraph.Define(g => bb = g.Backbuffer);

		Assert.Equal(ResourceKind.Backbuffer, bb!.Kind);
		Assert.True(bb.IsBackbuffer);
		Assert.True(bb.Domain.IsBackbuffer);
	}

	[Fact]
	public void Persistent_carries_ring_depth()
	{
		ResourceHandle? history = null;
		RenderGraph.Define(g => history = g.Persistent("exposure", Format.R16F, size: 2));

		Assert.Equal(ResourceKind.Persistent, history!.Kind);
		Assert.Equal(2, history.Count);
	}

	[Fact]
	public void Persistent_rejects_zero_ring_depth() =>
		Assert.Throws<ArgumentOutOfRangeException>(() =>
			RenderGraph.Define(g => g.Persistent("bad", Format.R16F, size: 0)));
}
