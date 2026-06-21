using Loom.Core.Graph;

namespace Loom.Tests;

/// <summary>Resource declaration, domains, handles — the nouns of the describe layer.</summary>
public class DomainAndResourceTests
{
	[Test]
	public void Domain_stores_its_resolution()
	{
		RenderGraph.Define(g =>
		{
			var world = g.Domain("world", 320, 180);
			world.Name.ShouldBe("world");
			world.Width.ShouldBe(320);
			world.Height.ShouldBe(180);
			world.IsBackbuffer.ShouldBeFalse();
		});
	}

	[Test]
	[Arguments(0, 180)]
	[Arguments(320, 0)]
	[Arguments(-1, 180)]
	public void Domain_rejects_nonpositive_dimensions(int width, int height) =>
		Should.Throw<ArgumentOutOfRangeException>(() =>
			RenderGraph.Define(g => g.Domain("bad", width, height)));

	[Test]
	public void Screen_is_the_backbuffer_domain()
	{
		RenderGraph.Define(g =>
		{
			var screen = g.Screen;
			screen.IsBackbuffer.ShouldBeTrue();
			screen.ShouldBeSameAs(g.Backbuffer.Domain); // the same native-resolution domain
		});
	}

	[Test]
	public void Relative_domain_records_its_scale_and_is_not_fixed()
	{
		RenderGraph.Define(g =>
		{
			var half = g.Domain("half", scale: 0.5f);
			half.RelativeScale.ShouldBe(0.5f);
			half.IsBackbuffer.ShouldBeFalse();
			half.Width.ShouldBe(0); // pixel size is backend-resolved against the swapchain
			half.Height.ShouldBe(0);
		});
	}

	[Test]
	[Arguments(0f)]
	[Arguments(-0.5f)]
	public void Relative_domain_rejects_nonpositive_scale(float scale) =>
		Should.Throw<ArgumentOutOfRangeException>(() =>
			RenderGraph.Define(g => g.Domain("bad", scale)));

	[Test]
	public void Relative_scale_distinguishes_the_graph_hash()
	{
		var a = RenderGraph.Define(g => { g.Domain("d", scale: 0.5f); });
		var b = RenderGraph.Define(g => { g.Domain("d", scale: 0.25f); });
		b.Hash().ShouldNotBe(a.Hash());
	}

	[Test]
	public void Domain_records_its_msaa_sample_count()
	{
		RenderGraph.Define(g =>
		{
			var scene = g.Domain("scene", 1280, 720, samples: 4);
			scene.Samples.ShouldBe(4);
			g.Domain("plain", 320, 180).Samples.ShouldBe(1); // default is single-sample
		});
	}

	[Test]
	[Arguments(0)]
	[Arguments(3)]
	[Arguments(6)]
	public void Domain_rejects_non_power_of_two_sample_count(int samples) =>
		Should.Throw<ArgumentOutOfRangeException>(() =>
			RenderGraph.Define(g => g.Domain("bad", 320, 180, samples)));

	[Test]
	public void Sample_count_distinguishes_the_graph_hash()
	{
		var a = RenderGraph.Define(g => { g.Domain("d", 320, 180, samples: 1); });
		var b = RenderGraph.Define(g => { g.Domain("d", 320, 180, samples: 4); });
		b.Hash().ShouldNotBe(a.Hash());
	}

	[Test]
	public void Duplicate_domain_name_throws() =>
		Should.Throw<InvalidOperationException>(() =>
			RenderGraph.Define(g =>
			{
				g.Domain("world", 320, 180);
				g.Domain("world", 640, 360);
			}));

	[Test]
	public void Resources_get_sequential_ids_in_declaration_order()
	{
		var graph = RenderGraph.Define(g =>
		{
			var world = g.Domain("world", 320, 180);
			world.Color("albedo", Format.Rgba8Srgb);
			world.Color("normal", Format.Rgb10A2);
			world.Depth("depth", Format.Depth32F);
		});

		graph.Resources.Length.ShouldBe(3);
		graph.Resources.Select(r => r.Id).ShouldBe(new[] { 0, 1, 2 });
		graph.Resources.Select(r => r.Name).ShouldBe(new[] { "albedo", "normal", "depth" });
	}

	[Test]
	public void Color_and_depth_carry_their_kind_and_format()
	{
		ResourceHandle? albedo = null, depth = null;
		RenderGraph.Define(g =>
		{
			var world = g.Domain("world", 320, 180);
			albedo = world.Color("albedo", Format.Rgba8Srgb);
			depth = world.Depth("depth", Format.Depth32F);
		});

		albedo!.Kind.ShouldBe(ResourceKind.Color);
		albedo.Format.ShouldBe(Format.Rgba8Srgb);
		depth!.Kind.ShouldBe(ResourceKind.Depth);
		depth.Format.ShouldBe(Format.Depth32F);
	}

	[Test]
	public void Handle_exposes_its_parent_domain()
	{
		Domain? world = null;
		ResourceHandle? albedo = null;
		RenderGraph.Define(g =>
		{
			world = g.Domain("world", 320, 180);
			albedo = world.Color("albedo", Format.Rgba8Srgb);
		});

		albedo!.Domain.ShouldBeSameAs(world);
	}

	[Test]
	public void Scaled_color_resolves_against_its_domain()
	{
		ResourceHandle? half = null;
		RenderGraph.Define(g =>
		{
			var world = g.Domain("world", 320, 180);
			half = world.Color("bloom_mip1", Format.Rgba16F, scale: 0.5f);
		});

		half!.Scale.ShouldBe(0.5f);
		half.Resolve().ShouldBe((160, 90));
	}

	[Test]
	public void Duplicate_resource_name_throws() =>
		Should.Throw<InvalidOperationException>(() =>
			RenderGraph.Define(g =>
			{
				var world = g.Domain("world", 320, 180);
				world.Color("dup", Format.Rgba8Srgb);
				world.Color("dup", Format.Rgba16F);
			}));

	[Test]
	public void Backbuffer_is_a_single_idempotent_handle()
	{
		var graph = RenderGraph.Define(g =>
		{
			var a = g.Backbuffer;
			var b = g.Backbuffer;
			b.ShouldBeSameAs(a);
		});

		// One backbuffer resource, one backbuffer domain — no duplicates from repeated access.
		graph.Resources.Where(r => r.IsBackbuffer).ShouldHaveSingleItem();
		graph.Domains.Where(d => d.IsBackbuffer).ShouldHaveSingleItem();
	}

	[Test]
	public void Backbuffer_handle_is_backbuffer_kind_in_native_domain()
	{
		ResourceHandle? bb = null;
		RenderGraph.Define(g => bb = g.Backbuffer);

		bb!.Kind.ShouldBe(ResourceKind.Backbuffer);
		bb.IsBackbuffer.ShouldBeTrue();
		bb.Domain.IsBackbuffer.ShouldBeTrue();
	}

	[Test]
	public void Persistent_is_single_buffered()
	{
		ResourceHandle? history = null;
		RenderGraph.Define(g => history = g.Persistent("exposure", Format.R16F));

		history!.Kind.ShouldBe(ResourceKind.Persistent);
		history.Count.ShouldBe(1);
	}

	[Test]
	public void Persistent_rejects_an_unimplemented_history_ring() =>
		// size > 1 over-promises a cross-frame ring the backends don't allocate yet — reject it (WS-2 honesty).
		Should.Throw<NotSupportedException>(() =>
			RenderGraph.Define(g => g.Persistent("exposure", Format.R16F, size: 2)));

	[Test]
	public void Persistent_rejects_zero_ring_depth() =>
		Should.Throw<ArgumentOutOfRangeException>(() =>
			RenderGraph.Define(g => g.Persistent("bad", Format.R16F, size: 0)));
}
