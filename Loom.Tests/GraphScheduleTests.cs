using Loom.Core;
using Loom.Graph;

namespace Loom.Tests;

/// <summary>The Execute layer's scheduling: dependency derivation from read/write edges + topological order.</summary>
public class GraphScheduleTests
{
	private static GraphSchedule Schedule(Action<GraphBuilder> author) =>
		GraphSchedule.Derive(RenderGraph.Define(author));

	/// <summary>Program indices of the prerequisites of the pass named <paramref name="name"/>, mapped to names.</summary>
	private static string[] DepsOf(GraphSchedule s, string name)
	{
		var pass = s.Passes.Single(p => p.Pass.Name == name);
		return [.. pass.DependsOn.Select(d => s.Graph.Passes[d].Name).Order()];
	}

	[Fact]
	public void Read_after_write_creates_a_dependency()
	{
		var s = Schedule(g =>
		{
			var world = g.Domain("world", 320, 180);
			var a = world.Color("a", Format.Rgba8Srgb);
			var b = world.Color("b", Format.Rgba8Srgb);
			g.Pass("producer").Writes(a);
			g.Pass("consumer").Samples(a).Writes(b);
		});

		Assert.Empty(DepsOf(s, "producer"));
		Assert.Equal(["producer"], DepsOf(s, "consumer"));
	}

	[Fact]
	public void Write_after_write_on_one_resource_depends_in_program_order()
	{
		// The two backbuffer writers from §6: ordered by program order via a WAW edge.
		var s = Schedule(g =>
		{
			var screen = g.Backbuffer;
			g.Pass("upscale").Writes(screen);
			g.Pass("hud").Writes(screen);
		});

		Assert.Empty(DepsOf(s, "upscale"));
		Assert.Equal(["upscale"], DepsOf(s, "hud"));
	}

	[Fact]
	public void Write_after_read_creates_a_dependency()
	{
		var s = Schedule(g =>
		{
			var world = g.Domain("world", 320, 180);
			var r = world.Color("r", Format.Rgba16F);
			var seed = world.Color("seed", Format.Rgba16F);
			g.Pass("seed").Writes(r);          // establish r so the reader isn't read-before-write
			g.Pass("reader").Samples(r).Writes(seed);
			g.Pass("rewriter").Writes(r);      // must wait for 'reader' (WAR)
		});

		Assert.Equal(["reader"], DepsOf(s, "rewriter"));
	}

	[Fact]
	public void Two_readers_of_the_same_resource_do_not_depend_on_each_other()
	{
		var s = Schedule(g =>
		{
			var world = g.Domain("world", 320, 180);
			var src = world.Color("src", Format.Rgba8Srgb);
			var o1 = world.Color("o1", Format.Rgba8Srgb);
			var o2 = world.Color("o2", Format.Rgba8Srgb);
			g.Pass("writer").Writes(src);
			g.Pass("readerA").Samples(src).Writes(o1);
			g.Pass("readerB").Samples(src).Writes(o2);
		});

		// Both read the writer's output; neither reads the other's, so they're independent.
		Assert.Equal(["writer"], DepsOf(s, "readerA"));
		Assert.Equal(["writer"], DepsOf(s, "readerB"));
	}

	[Fact]
	public void A_rewrite_after_multiple_readers_waits_on_the_readers_not_the_first_writer()
	{
		var s = Schedule(g =>
		{
			var world = g.Domain("world", 320, 180);
			var r = world.Color("r", Format.Rgba16F);
			var a = world.Color("a", Format.Rgba8Srgb);
			var b = world.Color("b", Format.Rgba8Srgb);
			g.Pass("write1").Writes(r);
			g.Pass("readA").Samples(r).Writes(a);
			g.Pass("readB").Samples(r).Writes(b);
			g.Pass("write2").Writes(r);
		});

		// Minimal precise WAR edges: the readers (which already wait on write1), not write1 itself.
		Assert.Equal(["readA", "readB"], DepsOf(s, "write2"));
	}

	[Fact]
	public void Blend_depends_on_the_prior_writer_and_becomes_the_next_last_writer()
	{
		var s = Schedule(g =>
		{
			var world = g.Domain("world", 320, 180);
			var lit = world.Color("lit", Format.Rgba16F);
			var screen = g.Backbuffer;
			g.Pass("produce").Writes(lit);
			g.Pass("accumulate").Blends(lit);     // RAW on produce; also a writer
			g.Pass("consume").Samples(lit).Writes(screen); // RAW on the blend (latest writer)
		});

		Assert.Equal(["produce"], DepsOf(s, "accumulate"));
		Assert.Equal(["accumulate"], DepsOf(s, "consume"));
	}

	[Fact]
	public void Depth_write_then_read_creates_a_dependency()
	{
		var s = Schedule(g =>
		{
			var world = g.Domain("world", 320, 180);
			var depth = world.Depth("depth", Format.Depth32F);
			var lit = world.Color("lit", Format.Rgba16F);
			g.Pass("gbuffer").WritesDepth(depth);
			g.Pass("transparent").ReadsDepth(depth).Writes(lit);
		});

		Assert.Equal(["gbuffer"], DepsOf(s, "transparent"));
	}

	[Fact]
	public void Reading_a_transient_before_it_is_written_throws()
	{
		var ex = Assert.Throws<GraphValidationException>(() =>
			Schedule(g =>
			{
				var world = g.Domain("world", 320, 180);
				var ghost = world.Color("ghost", Format.Rgba8Srgb);
				var o = world.Color("o", Format.Rgba8Srgb);
				g.Pass("p").Samples(ghost).Writes(o);
			}));

		Assert.Contains("ghost", ex.Message);
	}

	[Fact]
	public void Presenting_a_target_no_pass_writes_throws()
	{
		// You cannot present a resource nothing produces — the screen would show undefined contents (M7-A4).
		var ex = Assert.Throws<GraphValidationException>(() =>
			Schedule(g =>
			{
				var world = g.Domain("world", 320, 180);
				var screen = g.Backbuffer;
				var o = world.Color("o", Format.Rgba8Srgb);
				g.Pass("p").Writes(o); // produces 'o', never the backbuffer
				g.Present(screen);     // but presents the backbuffer, which nothing writes
			}));

		Assert.Contains("never written", ex.Message);
	}

	[Fact]
	public void Reading_a_persistent_before_writing_is_allowed()
	{
		// Persistent resources carry cross-frame history, so read-before-write is legal (Planning.md §8).
		var s = Schedule(g =>
		{
			var history = g.Persistent("exposure", Format.R16F);
			var o = g.Backbuffer;
			g.Pass("adapt").Samples(history).Writes(o);
		});

		Assert.Empty(DepsOf(s, "adapt"));
	}

	[Fact]
	public void Execution_order_places_every_pass_after_its_prerequisites()
	{
		var s = ReferenceFrame();
		var orderOf = s.Passes.ToDictionary(p => p.ProgramIndex, p => p.Order);
		foreach (var scheduled in s.Passes)
			foreach (var dep in scheduled.DependsOn)
				Assert.True(orderOf[dep] < scheduled.Order, $"'{s.Graph.Passes[dep].Name}' must precede '{scheduled.Pass.Name}'.");
	}

	[Fact]
	public void Reference_frame_derives_the_dependencies_section_6_describes()
	{
		var s = ReferenceFrame();

		Assert.Empty(DepsOf(s, "gbuffer"));
		Assert.Equal(["gbuffer"], DepsOf(s, "lighting"));            // samples the G-buffer
		Assert.Equal(["gbuffer", "lighting"], DepsOf(s, "transparent")); // RAW on lit, depth read on gbuffer
		Assert.Equal(["transparent"], DepsOf(s, "upscale"));        // samples the accumulated lit
		Assert.Equal(["upscale"], DepsOf(s, "hud"));                // WAW on the backbuffer, program order
	}

	// The §6 reference-frame shape (minus the bloom sub-graph, which is M5).
	private static GraphSchedule ReferenceFrame() => GraphSchedule.Derive(RenderGraph.Define(g =>
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
}
