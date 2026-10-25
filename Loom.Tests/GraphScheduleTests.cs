using Loom.Core.Graph;
using Loom.Core.Hosting;
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

	[Test]
	public void A_derive_time_rejection_is_catchable_as_an_authoring_mistake()
	{
		// Derive-time and record-time mistakes share one base, so an app can catch "I authored this wrong" once
		// instead of listing every exception type the engine and its backends can raise.
		var ex = Should.Throw<GraphValidationException>(() =>
			Schedule(g =>
			{
				var world = g.Domain("world", 320, 180);
				var ghost = world.Color("ghost", Format.Rgba8Srgb);
				g.Pass("p").Samples(ghost).Writes(world.Color("o", Format.Rgba8Srgb));
			}));

		ex.ShouldBeAssignableTo<LoomAuthoringException>();
	}

	[Test]
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

		DepsOf(s, "producer").ShouldBeEmpty();
		DepsOf(s, "consumer").ShouldBe(new[] { "producer" });
	}

	[Test]
	public void Write_after_write_on_one_resource_depends_in_program_order()
	{
		// The two backbuffer writers: ordered by program order via a WAW edge.
		var s = Schedule(g =>
		{
			var screen = g.Backbuffer;
			g.Pass("upscale").Writes(screen);
			g.Pass("hud").Writes(screen);
			g.Present(screen);
		});

		DepsOf(s, "upscale").ShouldBeEmpty();
		DepsOf(s, "hud").ShouldBe(new[] { "upscale" });
	}

	[Test]
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

		DepsOf(s, "rewriter").ShouldBe(new[] { "reader" });
	}

	[Test]
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
		DepsOf(s, "readerA").ShouldBe(new[] { "writer" });
		DepsOf(s, "readerB").ShouldBe(new[] { "writer" });
	}

	[Test]
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
		DepsOf(s, "write2").ShouldBe(new[] { "readA", "readB" });
	}

	[Test]
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
			g.Present(screen);
		});

		DepsOf(s, "accumulate").ShouldBe(new[] { "produce" });
		DepsOf(s, "consume").ShouldBe(new[] { "accumulate" });
	}

	[Test]
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

		DepsOf(s, "transparent").ShouldBe(new[] { "gbuffer" });
	}

	[Test]
	public void Reading_a_transient_before_it_is_written_throws()
	{
		var ex = Should.Throw<GraphValidationException>(() =>
			Schedule(g =>
			{
				var world = g.Domain("world", 320, 180);
				var ghost = world.Color("ghost", Format.Rgba8Srgb);
				var o = world.Color("o", Format.Rgba8Srgb);
				g.Pass("p").Samples(ghost).Writes(o);
			}));

		ex.Message.ShouldContain("ghost");
	}

	[Test]
	public void Presenting_a_target_no_pass_writes_throws()
	{
		// You cannot present a resource nothing produces — the screen would show undefined contents.
		var ex = Should.Throw<GraphValidationException>(() =>
			Schedule(g =>
			{
				var world = g.Domain("world", 320, 180);
				var screen = g.Backbuffer;
				var o = world.Color("o", Format.Rgba8Srgb);
				g.Pass("p").Writes(o); // produces 'o', never the backbuffer
				g.Present(screen);     // but presents the backbuffer, which nothing writes
			}));

		ex.Message.ShouldContain("never written");
	}

	[Test]
	public void Writing_the_backbuffer_without_presenting_it_throws()
	{
		// The blank-window footgun: the frame is rendered and then dropped, with no diagnostic anywhere. Nothing
		// legitimate does this, so it fails at derive rather than shipping a window that never updates.
		var ex = Should.Throw<GraphValidationException>(() =>
			Schedule(g =>
			{
				var world = g.Domain("world", 320, 180);
				var lit = world.Color("lit", Format.Rgba8Srgb);
				g.Pass("produce").Writes(lit);
				g.Pass("blit").Samples(lit).Writes(g.Backbuffer); // …and then never presents it
			}));

		ex.Message.ShouldContain("blit");
		ex.Message.ShouldContain("g.Present");
	}

	[Test]
	public void Presenting_something_other_than_the_written_backbuffer_throws()
	{
		var ex = Should.Throw<GraphValidationException>(() =>
			Schedule(g =>
			{
				var world = g.Domain("world", 320, 180);
				var other = world.Color("other", Format.Rgba8Srgb);
				g.Pass("blit").Writes(g.Backbuffer);
				g.Pass("side").Writes(other);
				g.Present(other); // the backbuffer's contents never reach the window
			}));

		ex.Message.ShouldContain("presents 'other'");
	}

	[Test]
	public void An_offscreen_graph_that_never_touches_the_backbuffer_is_fine()
	{
		// The check keys off a Backbuffer-kind resource existing at all, and a graph that never asks for one — every
		// offscreen render, including the golden tests — must stay unaffected.
		Should.NotThrow(() =>
			Schedule(g =>
			{
				var world = g.Domain("world", 320, 180);
				var o = world.Color("o", Format.Rgba8Srgb);
				g.Pass("p").Writes(o);
				g.Readback(o);
			}));
	}

	[Test]
	public void Reading_more_constants_buffers_than_a_pass_can_bind_throws()
	{
		// Past the cap the extras used to be dropped on every backend, mis-binding each slot after it. The count is
		// the same everywhere, so the rejection belongs at derive rather than in three record paths.
		var over = GraphLimits.MaxUniformBuffersPerPass + 1;
		var ex = Should.Throw<GraphValidationException>(() =>
			Schedule(g =>
			{
				var world = g.Domain("world", 320, 180);
				var pass = g.Pass("post").Writes(world.Color("o", Format.Rgba8Srgb));
				for (var i = 0; i < over; i++)
					pass.Reads(g.Constants<System.Numerics.Vector4>($"params{i}"));
			}));

		ex.Message.ShouldContain("post");
		ex.Message.ShouldContain(over.ToString());
		ex.Message.ShouldContain(GraphLimits.MaxUniformBuffersPerPass.ToString());
	}

	[Test]
	public void Reading_exactly_the_cap_is_allowed()
	{
		// The boundary: the cap is what the backends' slot arrays are sized to, so exactly that many must derive.
		Should.NotThrow(() =>
			Schedule(g =>
			{
				var world = g.Domain("world", 320, 180);
				var pass = g.Pass("post").Writes(world.Color("o", Format.Rgba8Srgb));
				for (var i = 0; i < GraphLimits.MaxUniformBuffersPerPass; i++)
					pass.Reads(g.Constants<System.Numerics.Vector4>($"params{i}"));
			}));
	}

	[Test]
	public void Attaching_targets_of_different_extents_to_one_pass_throws()
	{
		// A pass renders through one viewport and render area, and the backends disagreed about which attachment
		// supplied it — two read the first, one the last colour target. Every choice is wrong for some attachment, so
		// there is nothing to converge on: reject the declaration instead, once, before a frame is recorded.
		var ex = Should.Throw<GraphValidationException>(() =>
			Schedule(g =>
			{
				var world = g.Domain("world", 320, 180);
				var hud = g.Domain("hud", 1280, 720);
				g.Pass("mrt").Writes(world.Color("a", Format.Rgba8Srgb), hud.Color("b", Format.Rgba8Srgb));
			}));

		ex.Message.ShouldContain("mrt");
		ex.Message.ShouldContain("world");
		ex.Message.ShouldContain("hud");
	}

	[Test]
	public void Attaching_targets_at_different_scales_within_one_domain_throws()
	{
		// The same hazard without two domains: a mip-chain scale makes a sibling target a different size.
		var ex = Should.Throw<GraphValidationException>(() =>
			Schedule(g =>
			{
				var world = g.Domain("world", 320, 180);
				g.Pass("mrt").Writes(world.Color("full", Format.Rgba8Srgb), world.Color("half", Format.Rgba8Srgb, 0.5f));
			}));

		ex.Message.ShouldContain("half");
	}

	[Test]
	public void Multiple_attachments_of_one_extent_are_allowed()
	{
		// The boundary that matters: real MRT — several colour targets plus depth, all one domain at one scale — must
		// still derive. g.Screen is the backbuffer's own domain, so mixing the backbuffer with screen-sized siblings is
		// one extent too, and that pattern is common enough that rejecting it would be a regression.
		Should.NotThrow(() =>
			Schedule(g =>
			{
				var screen = g.Backbuffer;
				g.Pass("gbuffer")
					.Writes(screen, g.Screen.Color("normal", Format.Rgba16F), g.Screen.Color("position", Format.Rgba16F))
					.WritesDepth(g.Screen.Depth("depth", Format.Depth32F));
				g.Present(screen);
			}));
	}

	[Test]
	public void A_raster_pass_with_no_attachment_throws()
	{
		// DirectX 12 threw for this while recording; Vulkan and WebGPU began a 0x0 pass and drew nothing, which is
		// indistinguishable from a pass whose draws are broken. Rejected for all three, before a frame is recorded.
		var ex = Should.Throw<GraphValidationException>(() =>
			Schedule(g =>
			{
				var screen = g.Backbuffer;
				g.Pass("nowhere").Samples(g.Screen.Color("src", Format.Rgba8Srgb));
				g.Pass("view").Writes(screen);
				g.Present(screen);
			}));

		ex.Message.ShouldContain("nowhere");
		ex.Message.ShouldContain("g.Compute");
	}

	[Test]
	public void Writing_more_colour_attachments_than_a_pass_can_bind_throws()
	{
		// Eight is the portable ceiling — D3D12's simultaneous render targets and WebGPU's maxColorAttachments — while
		// Vulkan usually allows more, so this used to derive and record on one backend and fail on the others.
		var over = GraphLimits.MaxColorAttachmentsPerPass + 1;
		var ex = Should.Throw<GraphValidationException>(() =>
			Schedule(g =>
			{
				var pass = g.Pass("fat");
				for (var i = 0; i < over; i++)
					pass.Writes(g.Screen.Color($"t{i}", Format.Rgba8Srgb));
			}));

		ex.Message.ShouldContain("fat");
		ex.Message.ShouldContain(over.ToString());
		ex.Message.ShouldContain(GraphLimits.MaxColorAttachmentsPerPass.ToString());
	}

	[Test]
	public void Writing_exactly_the_colour_attachment_cap_is_allowed()
	{
		// The boundary: the cap is what the backends' attachment arrays are sized to, so exactly that many must derive.
		Should.NotThrow(() =>
			Schedule(g =>
			{
				var pass = g.Pass("wide");
				for (var i = 0; i < GraphLimits.MaxColorAttachmentsPerPass; i++)
					pass.Writes(g.Screen.Color($"t{i}", Format.Rgba8Srgb));
			}));
	}

	[Test]
	public void A_compute_pass_may_touch_resources_of_any_extent()
	{
		// Compute has no attachments and no render area, so the extent rule must not reach it.
		Should.NotThrow(() =>
			Schedule(g =>
			{
				var world = g.Domain("world", 320, 180);
				var hud = g.Domain("hud", 1280, 720);
				g.Compute("mix").ReadWrites(world.Color("a", Format.Rgba16F), hud.Color("b", Format.Rgba16F));
			}));
	}

	[Test]
	public void Async_compute_is_rejected_until_multi_queue_lands()
	{
		// Queue.AsyncCompute is reserved but not yet derived or backed — declaring it must fail loud rather than
		// silently scheduling the pass on the graphics queue and implying overlap (WS-2 honesty).
		var ex = Should.Throw<GraphValidationException>(() =>
			Schedule(g =>
			{
				var buffer = g.Buffer("particles", 1024);
				g.Compute("sim").ReadWrites(buffer).Queue(Queue.AsyncCompute);
			}));

		ex.Message.ShouldContain("AsyncCompute");
	}

	[Test]
	public void Sampling_the_backbuffer_is_rejected()
	{
		// The swapchain image is acquired fresh each frame, so it has no stable contents to sample — and each backend
		// resolved it differently when asked to. Rejecting at derive gives one answer everywhere, before recording.
		var ex = Should.Throw<GraphValidationException>(() =>
			Schedule(g =>
			{
				var world = g.Domain("world", 320, 180);
				var lit = world.Color("lit", Format.Rgba8Srgb);
				g.Pass("produce").Writes(lit);
				g.Pass("post").Samples(g.Backbuffer).Writes(lit);
				g.Present(g.Backbuffer);
			}));

		ex.Message.ShouldContain(".Samples");
		ex.Message.ShouldContain("backbuffer");
	}

	[Test]
	public void Read_writing_the_backbuffer_as_storage_is_rejected()
	{
		var ex = Should.Throw<GraphValidationException>(() =>
			Schedule(g =>
			{
				g.Compute("post").ReadWrites(g.Backbuffer);
				g.Present(g.Backbuffer);
			}));

		ex.Message.ShouldContain(".ReadWrites");
		ex.Message.ShouldContain("backbuffer");
	}

	[Test]
	public void Writing_blending_and_presenting_the_backbuffer_stay_legal()
	{
		// The guard must reject only the two read shapes — the backbuffer's whole purpose is to be written and
		// presented, and .Blends onto it is how overlays composite.
		var s = Schedule(g =>
		{
			var screen = g.Backbuffer;
			g.Pass("scene").Writes(screen);
			g.Pass("overlay").Blends(screen);
			g.Present(screen);
		});

		s.Passes.Length.ShouldBe(2);
	}

	[Test]
	public void Compute_then_compute_on_one_storage_buffer_depends_in_program_order()
	{
		// The GPU-particle / sim-step shape: a second compute that .ReadWrites the same storage buffer must wait
		// on the first (a storage read-modify-write is both reader and writer, so this is a RAW+WAW hazard).
		var s = Schedule(g =>
		{
			var buffer = g.Buffer("sim", 1024);
			g.Compute("step1").ReadWrites(buffer);
			g.Compute("step2").ReadWrites(buffer);
		});

		DepsOf(s, "step2").ShouldBe(["step1"]);
	}

	[Test]
	public void Ping_pong_handle_swap_chains_dependencies_through_both_buffers()
	{
		// The ping-pong idiom (bloom / JFA): two physical targets and a (read, write) tuple swapped between
		// passes. The derivation must follow the handles — pass 'v' reads what 'h' wrote.
		var s = Schedule(g =>
		{
			var world = g.Domain("world", 64, 64);
			var a = world.Color("a", Format.Rgba16F);
			var b = world.Color("b", Format.Rgba16F);
			g.Pass("seed").Writes(a);          // produce a
			g.Pass("h").Samples(a).Writes(b);  // a -> b
			g.Pass("v").Samples(b).Writes(a);  // b -> a (read/write swapped)
		});

		DepsOf(s, "h").ShouldBe(["seed"]);
		DepsOf(s, "v").ShouldBe(["h"]);
	}

	[Test]
	public void Reading_a_persistent_before_writing_is_allowed()
	{
		// Persistent resources carry cross-frame history, so read-before-write is legal.
		var s = Schedule(g =>
		{
			var history = g.Persistent("exposure", Format.R16F);
			var o = g.Backbuffer;
			g.Pass("adapt").Samples(history).Writes(o);
			g.Present(o);
		});

		DepsOf(s, "adapt").ShouldBeEmpty();
	}

	[Test]
	public void Execution_order_places_every_pass_after_its_prerequisites()
	{
		var s = ReferenceFrame();
		var orderOf = s.Passes.ToDictionary(p => p.ProgramIndex, p => p.Order);
		foreach (var scheduled in s.Passes)
			foreach (var dep in scheduled.DependsOn)
				(orderOf[dep] < scheduled.Order).ShouldBeTrue($"'{s.Graph.Passes[dep].Name}' must precede '{scheduled.Pass.Name}'.");
	}

	[Test]
	public void Reference_frame_derives_the_dependencies_section_6_describes()
	{
		var s = ReferenceFrame();

		DepsOf(s, "gbuffer").ShouldBeEmpty();
		DepsOf(s, "lighting").ShouldBe(new[] { "gbuffer" });            // samples the G-buffer
		DepsOf(s, "transparent").ShouldBe(new[] { "gbuffer", "lighting" }); // RAW on lit, depth read on gbuffer
		DepsOf(s, "upscale").ShouldBe(new[] { "transparent" });        // samples the accumulated lit
		DepsOf(s, "hud").ShouldBe(new[] { "upscale" });                // WAW on the backbuffer, program order
	}

	// The reference-frame shape (minus the bloom sub-graph).
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
