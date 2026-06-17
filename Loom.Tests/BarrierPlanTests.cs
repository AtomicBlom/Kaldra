using Loom.Core;
using Loom.Graph;

namespace Loom.Tests;

/// <summary>The Execute layer's barrier derivation: layout transitions + attachment load/store ops.</summary>
public class BarrierPlanTests
{
	private static PassBarriers Pass(BarrierPlan plan, string name) =>
		plan.Passes.Single(p => p.Pass.Name == name);

	private static bool HasTransition(PassBarriers pb, string resource, ResourceState from, ResourceState to) =>
		pb.Transitions.Any(t => t.Resource.Name == resource && t.From == from && t.To == to);

	private static AttachmentAction Attachment(PassBarriers pb, string resource) =>
		pb.Attachments.Single(a => a.Resource.Name == resource);

	private static BarrierPlan ReferenceFrame() => BarrierPlan.Derive(GraphSchedule.Derive(RenderGraph.Define(g =>
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
	})));

	[Fact]
	public void First_write_of_a_fresh_target_clears_and_stores_when_read_later()
	{
		var gbuffer = Pass(ReferenceFrame(), "gbuffer");

		Assert.True(HasTransition(gbuffer, "albedo", ResourceState.Undefined, ResourceState.ColorAttachment));
		var albedo = Attachment(gbuffer, "albedo");
		Assert.Equal(LoadAction.Clear, albedo.Load);
		Assert.Equal(StoreAction.Store, albedo.Store); // sampled by lighting
	}

	[Fact]
	public void Gbuffer_targets_flip_to_shader_read_for_lighting()
	{
		var lighting = Pass(ReferenceFrame(), "lighting");

		// "The G-buffer targets flip COLOR_ATTACHMENT -> SHADER_READ between gbuffer and lighting." (§6)
		Assert.True(HasTransition(lighting, "albedo", ResourceState.ColorAttachment, ResourceState.ShaderRead));
		Assert.True(HasTransition(lighting, "normal", ResourceState.ColorAttachment, ResourceState.ShaderRead));
		Assert.True(HasTransition(lighting, "material", ResourceState.ColorAttachment, ResourceState.ShaderRead));
	}

	[Fact]
	public void Blend_loads_existing_contents_and_emits_a_same_layout_write_hazard_barrier()
	{
		var transparent = Pass(ReferenceFrame(), "transparent");

		// LoadOp.Load to accumulate onto the lit result; the barrier is a same-layout memory hazard (RAW).
		Assert.Equal(LoadAction.Load, Attachment(transparent, "lit").Load);
		Assert.True(HasTransition(transparent, "lit", ResourceState.ColorAttachment, ResourceState.ColorAttachment));
	}

	[Fact]
	public void Depth_becomes_read_only_for_the_transparent_pass()
	{
		var transparent = Pass(ReferenceFrame(), "transparent");

		Assert.True(HasTransition(transparent, "depth", ResourceState.ShaderRead, ResourceState.DepthReadOnly));
		var depth = Attachment(transparent, "depth");
		Assert.Equal(ResourceState.DepthReadOnly, depth.State);
		Assert.Equal(LoadAction.Load, depth.Load);      // the existing depth is the test input
		Assert.Equal(StoreAction.DontCare, depth.Store); // read-only: nothing to store
	}

	[Fact]
	public void First_backbuffer_write_clears_second_loads_over_it()
	{
		var plan = ReferenceFrame();

		Assert.Equal(LoadAction.Clear, Attachment(Pass(plan, "upscale"), "<backbuffer>").Load);
		// The HUD draws on top of the upscaled image, so it must preserve (load) existing contents.
		Assert.Equal(LoadAction.Load, Attachment(Pass(plan, "hud"), "<backbuffer>").Load);
		// Same-layout WAW barrier orders hud after upscale on the backbuffer.
		Assert.True(HasTransition(Pass(plan, "hud"), "<backbuffer>", ResourceState.ColorAttachment, ResourceState.ColorAttachment));
	}

	[Fact]
	public void Present_target_gets_a_terminal_transition_to_present()
	{
		var plan = ReferenceFrame();
		var terminal = Assert.Single(plan.FinalTransitions);
		Assert.True(terminal.Resource.IsBackbuffer);
		Assert.Equal(ResourceState.ColorAttachment, terminal.From);
		Assert.Equal(ResourceState.Present, terminal.To);
	}

	[Fact]
	public void Read_after_read_emits_no_barrier_for_the_shared_resource()
	{
		var plan = BarrierPlan.Derive(GraphSchedule.Derive(RenderGraph.Define(g =>
		{
			var world = g.Domain("world", 320, 180);
			var src = world.Color("src", Format.Rgba8Srgb);
			var o1 = world.Color("o1", Format.Rgba8Srgb);
			var o2 = world.Color("o2", Format.Rgba8Srgb);
			g.Pass("writer").Writes(src);
			g.Pass("readerA").Samples(src).Writes(o1);
			g.Pass("readerB").Samples(src).Writes(o2);
		})));

		// readerA transitions src ColorAttachment -> ShaderRead; readerB finds it already ShaderRead.
		Assert.True(HasTransition(Pass(plan, "readerA"), "src", ResourceState.ColorAttachment, ResourceState.ShaderRead));
		Assert.DoesNotContain(Pass(plan, "readerB").Transitions, t => t.Resource.Name == "src");
	}

	[Fact]
	public void Dead_target_that_is_never_read_is_not_stored()
	{
		var plan = BarrierPlan.Derive(GraphSchedule.Derive(RenderGraph.Define(g =>
		{
			var world = g.Domain("world", 320, 180);
			var scratch = world.Color("scratch", Format.Rgba16F);
			g.Pass("only").Writes(scratch); // written, never read, not presented
		})));

		Assert.Equal(StoreAction.DontCare, Attachment(Pass(plan, "only"), "scratch").Store);
	}

	[Fact]
	public void Instance_buffer_filled_by_compute_transitions_storage_to_vertex_input_for_the_draw()
	{
		// M12 GPU-driven instancing reuses the GPU-particle edge: a compute .ReadWrites the instance buffer, the
		// draw .ReadsVertex it, so the graph derives the same Storage -> VertexInput barrier (no graphics-API type
		// in the describe/execute layers — the buffer is just a resource with read/write edges).
		var plan = BarrierPlan.Derive(GraphSchedule.Derive(RenderGraph.Define(g =>
		{
			var instances = g.Buffer("instances", 1024 * 64); // a 4x4 matrix per instance
			var screen = g.Backbuffer;
			g.Compute("place").ReadWrites(instances);
			g.Pass("foliage").Writes(screen).ReadsVertex(instances);
			g.Present(screen);
		})));

		Assert.True(HasTransition(Pass(plan, "foliage"), "instances", ResourceState.Storage, ResourceState.VertexInput));
	}
}
