using Loom.Core;

namespace Loom.Tests;

/// <summary>The pass verbs — the only thing the author states, and what derivation feeds on.</summary>
public class PassBuilderTests
{
	private static RenderGraph SinglePass(Action<GraphBuilder, Domain> body) =>
		RenderGraph.Define(g =>
		{
			var world = g.Domain("world", 320, 180);
			body(g, world);
		});

	[Fact]
	public void Writes_records_a_write_edge_per_resource()
	{
		var graph = SinglePass((g, world) =>
		{
			var a = world.Color("a", Format.Rgba8Srgb);
			var b = world.Color("b", Format.Rgba8Srgb);
			g.Pass("p").Writes(a, b);
		});

		var pass = Assert.Single(graph.Passes);
		Assert.Equal(["a", "b"], pass.ResourcesWith(AccessKind.Write).Select(r => r.Name));
	}

	[Fact]
	public void All_verbs_record_their_distinct_access_kinds()
	{
		var graph = SinglePass((g, world) =>
		{
			var color = world.Color("color", Format.Rgba16F);
			var sampled = world.Color("sampled", Format.Rgba8Srgb);
			var depth = world.Depth("depth", Format.Depth32F);
			var storage = world.Color("storage", Format.Rgba16F);

			g.Pass("p")
				.Samples(sampled)
				.Blends(color)
				.WritesDepth(depth)
				.ReadWrites(storage);

			// ReadsDepth lives on a second pass to avoid a write+read conflict on one resource.
			g.Pass("q").ReadsDepth(depth);
		});

		var p = graph.Passes[0];
		Assert.Equal("sampled", Assert.Single(p.ResourcesWith(AccessKind.Sample)).Name);
		Assert.Equal("color", Assert.Single(p.ResourcesWith(AccessKind.Blend)).Name);
		Assert.Equal("depth", Assert.Single(p.ResourcesWith(AccessKind.WriteDepth)).Name);
		Assert.Equal("storage", Assert.Single(p.ResourcesWith(AccessKind.ReadWrite)).Name);
		Assert.Equal("depth", Assert.Single(graph.Passes[1].ResourcesWith(AccessKind.ReadDepth)).Name);
	}

	/// <summary>
	/// The load-bearing distinction (Planning.md §8): producing vs accumulating must stay distinguishable
	/// on the same resource, because barrier derivation depends on it.
	/// </summary>
	[Fact]
	public void Writes_and_Blends_are_distinguishable_on_the_same_resource()
	{
		var graph = SinglePass((g, world) =>
		{
			var lit = world.Color("lit", Format.Rgba16F);
			g.Pass("produce").Writes(lit);
			g.Pass("accumulate").Blends(lit);
		});

		Assert.Equal(AccessKind.Write, Assert.Single(graph.Passes[0].Accesses).Kind);
		Assert.Equal(AccessKind.Blend, Assert.Single(graph.Passes[1].Accesses).Kind);
	}

	[Fact]
	public void Pass_defaults_to_the_graphics_queue()
	{
		var graph = SinglePass((g, _) => g.Pass("p"));
		Assert.Equal(Queue.Graphics, Assert.Single(graph.Passes).Queue);
	}

	[Fact]
	public void Queue_assignment_is_recorded()
	{
		var graph = SinglePass((g, _) => g.Compute("c").Queue(Queue.AsyncCompute));
		Assert.Equal(Queue.AsyncCompute, Assert.Single(graph.Passes).Queue);
	}

	[Fact]
	public void Raster_and_compute_passes_carry_their_kind()
	{
		var graph = SinglePass((g, _) =>
		{
			g.Pass("raster");
			g.Compute("compute");
		});

		Assert.Equal(PassKind.Raster, graph.Passes[0].Kind);
		Assert.Equal(PassKind.Compute, graph.Passes[1].Kind);
	}

	[Fact]
	public void Draw_on_a_compute_pass_throws() =>
		Assert.Throws<InvalidOperationException>(() =>
			SinglePass((g, _) => g.Compute("c").Draw(_ => { })));

	[Fact]
	public void Dispatch_on_a_raster_pass_throws() =>
		Assert.Throws<InvalidOperationException>(() =>
			SinglePass((g, _) => g.Pass("p").Dispatch(_ => { })));

	[Fact]
	public void Record_callback_is_retained()
	{
		var graph = SinglePass((g, _) => g.Pass("p").Draw(_ => { }));
		Assert.NotNull(Assert.Single(graph.Passes).Record);
	}

	[Fact]
	public void Pass_without_a_callback_has_null_record()
	{
		var graph = SinglePass((g, _) => g.Pass("p"));
		Assert.Null(Assert.Single(graph.Passes).Record);
	}

	[Fact]
	public void Duplicate_pass_name_throws() =>
		Assert.Throws<InvalidOperationException>(() =>
			SinglePass((g, _) =>
			{
				g.Pass("p");
				g.Pass("p");
			}));
}
