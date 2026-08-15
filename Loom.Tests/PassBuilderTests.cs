using Loom.Core.Graph;

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

	[Test]
	public void Writes_records_a_write_edge_per_resource()
	{
		var graph = SinglePass((g, world) =>
		{
			var a = world.Color("a", Format.Rgba8Srgb);
			var b = world.Color("b", Format.Rgba8Srgb);
			g.Pass("p").Writes(a, b);
		});

		var pass = graph.Passes.ShouldHaveSingleItem();
		pass.ResourcesWith(AccessKind.Write).Select(r => r.Name).ShouldBe(new[] { "a", "b" });
	}

	[Test]
	public void All_verbs_record_their_distinct_access_kinds()
	{
		var graph = SinglePass((g, world) =>
		{
			var color = world.Color("color", Format.Rgba16F);
			var sampled = world.Color("sampled", Format.Rgba8Srgb);
			var depth = world.Depth("depth", Format.Depth32F);
			var storage = world.Color("storage", Format.Rgba16F);

			var instances = g.Buffer("instances", 1024);
			var arguments = g.Buffer("arguments", 20);

			g.Pass("p")
				.Samples(sampled)
				.Blends(color)
				.WritesDepth(depth)
				.ReadWrites(storage)
				.ReadsVertex(instances)
				.ReadsIndirect(arguments);

			// ReadsDepth lives on a second pass to avoid a write+read conflict on one resource.
			g.Pass("q").ReadsDepth(depth);
		});

		var p = graph.Passes[0];
		p.ResourcesWith(AccessKind.Sample).ShouldHaveSingleItem().Name.ShouldBe("sampled");
		p.ResourcesWith(AccessKind.Blend).ShouldHaveSingleItem().Name.ShouldBe("color");
		p.ResourcesWith(AccessKind.WriteDepth).ShouldHaveSingleItem().Name.ShouldBe("depth");
		p.ResourcesWith(AccessKind.ReadWrite).ShouldHaveSingleItem().Name.ShouldBe("storage");
		p.ResourcesWith(AccessKind.ReadVertex).ShouldHaveSingleItem().Name.ShouldBe("instances");
		p.ResourcesWith(AccessKind.ReadIndirect).ShouldHaveSingleItem().Name.ShouldBe("arguments");
		graph.Passes[1].ResourcesWith(AccessKind.ReadDepth).ShouldHaveSingleItem().Name.ShouldBe("depth");
	}

	/// <summary>
	/// The load-bearing distinction: producing vs accumulating must stay distinguishable
	/// on the same resource, because barrier derivation depends on it.
	/// </summary>
	[Test]
	public void Writes_and_Blends_are_distinguishable_on_the_same_resource()
	{
		var graph = SinglePass((g, world) =>
		{
			var lit = world.Color("lit", Format.Rgba16F);
			g.Pass("produce").Writes(lit);
			g.Pass("accumulate").Blends(lit);
		});

		graph.Passes[0].Accesses.ShouldHaveSingleItem().Kind.ShouldBe(AccessKind.Write);
		graph.Passes[1].Accesses.ShouldHaveSingleItem().Kind.ShouldBe(AccessKind.Blend);
	}

	[Test]
	public void Pass_defaults_to_the_graphics_queue()
	{
		var graph = SinglePass((g, _) => g.Pass("p"));
		graph.Passes.ShouldHaveSingleItem().Queue.ShouldBe(Queue.Graphics);
	}

	[Test]
	public void Queue_assignment_is_recorded()
	{
		var graph = SinglePass((g, _) => g.Compute("c").Queue(Queue.AsyncCompute));
		graph.Passes.ShouldHaveSingleItem().Queue.ShouldBe(Queue.AsyncCompute);
	}

	[Test]
	public void Raster_and_compute_passes_carry_their_kind()
	{
		var graph = SinglePass((g, _) =>
		{
			g.Pass("raster");
			g.Compute("compute");
		});

		graph.Passes[0].Kind.ShouldBe(PassKind.Raster);
		graph.Passes[1].Kind.ShouldBe(PassKind.Compute);
	}

	[Test]
	public void Draw_on_a_compute_pass_throws() =>
		Should.Throw<InvalidOperationException>(() =>
			SinglePass((g, _) => g.Compute("c").Draw(_ => { })));

	[Test]
	public void Dispatch_on_a_raster_pass_throws() =>
		Should.Throw<InvalidOperationException>(() =>
			SinglePass((g, _) => g.Pass("p").Dispatch(_ => { })));

	[Test]
	public void Record_callback_is_retained()
	{
		var graph = SinglePass((g, _) => g.Pass("p").Draw(_ => { }));
		graph.Passes.ShouldHaveSingleItem().Record.ShouldNotBeNull();
	}

	[Test]
	public void Pass_without_a_callback_has_null_record()
	{
		var graph = SinglePass((g, _) => g.Pass("p"));
		graph.Passes.ShouldHaveSingleItem().Record.ShouldBeNull();
	}

	[Test]
	public void Duplicate_pass_name_throws() =>
		Should.Throw<InvalidOperationException>(() =>
			SinglePass((g, _) =>
			{
				g.Pass("p");
				g.Pass("p");
			}));
}
