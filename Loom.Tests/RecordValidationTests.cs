using Loom.Core.Geometry;
using Loom.Core.Graph;
using Loom.Core.Hosting;
using Loom.Core.Materials;
using Loom.Graph;

namespace Loom.Tests;

/// <summary>
/// Record-time validation: the recording contradicting what its pass declared. Deriving the graph already rejects a
/// declaration that contradicts itself; these are the mistakes that only show up once a pass starts issuing draws, and
/// which used to produce a plausible-looking wrong frame instead of an error.
/// <para>
/// Device-free on purpose. <see cref="RecordValidator"/> speaks only in neutral terms, so the behaviour can be pinned
/// here rather than only through a GPU-gated render — and the gallery rendering green proves the checks do not
/// misfire, while these prove they actually fire.
/// </para>
/// </summary>
public class RecordValidationTests
{
	// A pass built through the public authoring API, so the accesses are exactly what an app would declare.
	private static GraphPass PassOf(Action<GraphBuilder> author, string name = "p")
	{
		var graph = RenderGraph.Define(author);
		return graph.Passes.Single(p => p.Name == name);
	}

	private static RecordValidator ValidatorOn(GraphPass pass, TechniqueRegistry? techniques = null, bool enabled = true)
	{
		var validator = new RecordValidator(enabled, techniques ?? new TechniqueRegistry());
		validator.BeginPass(pass);
		return validator;
	}

	// A pass that samples 'declared' and writes 'out', plus the handle for an undeclared sibling target.
	private static (GraphPass Pass, ResourceHandle Declared, ResourceHandle Undeclared) SamplingPass()
	{
		ResourceHandle declared = null!, undeclared = null!;
		var pass = PassOf(g =>
		{
			var world = g.Domain("world", 64, 64);
			declared = world.Color("declared", Format.Rgba8Srgb);
			undeclared = world.Color("undeclared", Format.Rgba8Srgb);
			g.Pass("seed").Writes((ColorHandle)declared);
			g.Pass("other").Writes((ColorHandle)undeclared);
			g.Pass("p").Samples((ColorHandle)declared).Writes(world.Color("out", Format.Rgba8Srgb));
		});
		return (pass, declared, undeclared);
	}

	private static TechniqueRegistry RegistryWith(string name, TechniqueDescriptor descriptor, TechniqueShape shape)
	{
		var registry = new TechniqueRegistry();
		registry.Register(name, descriptor, shape);
		return registry;
	}

	[Test]
	public void Binding_a_resource_the_pass_never_declared_throws()
	{
		// The headline footgun: the pass forgot .Samples, so the graph derived no barrier or layout transition for the
		// resource and the draw reads it in whatever state the previous pass happened to leave it in.
		var (pass, _, undeclared) = SamplingPass();
		var validator = ValidatorOn(pass);

		var ex = Should.Throw<PassBindingException>(() => validator.FullScreen("blit", [undeclared]));

		ex.Pass.ShouldBe("p");
		ex.Resource.ShouldBe("undeclared");
		ex.Message.ShouldContain(".Samples(undeclared)");
		ex.ShouldBeAssignableTo<LoomAuthoringException>();
	}

	[Test]
	public void Drawing_indirectly_from_a_buffer_the_pass_never_declared_throws()
	{
		// An indirect draw whose argument buffer was not declared reads counts the graph derived no hand-off for —
		// so the command processor may fetch them before the compute that wrote them retired. Silent on the GPU,
		// which is why it is reported here, naming the verb that fixes it.
		BufferHandle arguments = null!;
		var pass = PassOf(g =>
		{
			var world = g.Domain("world", 64, 64);
			arguments = g.Buffer("draw.args", 20);
			g.Compute("fill").ReadWrites(arguments);
			g.Pass("p").Writes(world.Color("out", Format.Rgba8Srgb));
		});
		var validator = ValidatorOn(pass);

		// Built inside the lambda: DrawCall is a ref struct, so it cannot be captured by one.
		var ex = Should.Throw<PassBindingException>(() =>
			validator.Draw(new DrawCall { Technique = "mesh", Mesh = new Mesh(1), Instances = DrawCall.Indirect(arguments) }));

		ex.Resource.ShouldBe("draw.args");
		ex.Message.ShouldContain(".ReadsIndirect(draw.args)");
	}

	[Test]
	public void Binding_a_declared_resource_is_accepted()
	{
		var (pass, declared, _) = SamplingPass();
		var validator = ValidatorOn(pass);

		Should.NotThrow(() => validator.FullScreen("blit", [declared]));
	}

	[Test]
	public void A_resource_declared_with_the_wrong_access_kind_still_throws()
	{
		// .Samples and .ReadWrites are different edges deriving different barriers, so declaring one does not license
		// binding the resource through the other.
		var (pass, declared, _) = SamplingPass();
		var validator = ValidatorOn(pass);

		Should.Throw<PassBindingException>(() => validator.Dispatch("sim", default, [declared]));
	}

	[Test]
	public void A_dispatch_validates_both_its_sampled_and_its_storage_bindings()
	{
		BufferHandle storage = null!;
		ResourceHandle undeclared = null!;
		var pass = PassOf(g =>
		{
			storage = g.Buffer<System.Numerics.Vector4>("particles", 16);
			undeclared = g.Buffer<System.Numerics.Vector4>("stray", 16);
			g.Compute("p").ReadWrites(storage);
		});
		var validator = ValidatorOn(pass);

		Should.NotThrow(() => validator.Dispatch("sim", default, [storage]));
		Should.Throw<PassBindingException>(() => validator.Dispatch("sim", default, [undeclared]));
	}

	[Test]
	public void Handing_a_mesh_to_a_technique_that_fabricates_its_own_vertices_throws()
	{
		// A fullscreen technique reads no per-vertex data, so the mesh would be ignored and the draw would render the
		// technique's own three fabricated verts — geometry silently absent rather than an error.
		var (pass, _, _) = SamplingPass();
		var registry = RegistryWith("post", new TechniqueDescriptor { Shader = "post" }, default);
		var validator = ValidatorOn(pass, registry);

		var ex = Should.Throw<TechniqueMismatchException>(() =>
			validator.Draw(new DrawCall { Technique = "post", Mesh = new Mesh(1) }));

		ex.Technique.ShouldBe("post");
		ex.Message.ShouldContain("HasVertexInput");
	}

	[Test]
	public void A_mesh_technique_accepts_a_mesh()
	{
		var (pass, _, _) = SamplingPass();
		var registry = RegistryWith("mesh", new TechniqueDescriptor { Shader = "mesh", HasVertexInput = true }, default);
		var validator = ValidatorOn(pass, registry);

		Should.NotThrow(() => validator.Draw(new DrawCall { Technique = "mesh", Mesh = new Mesh(1) }));
	}

	[Test]
	public void A_technique_with_only_an_explicit_layout_also_takes_vertices()
	{
		// HasVertexInput and an explicit Layout are two ways to say the same thing; both must license a mesh, which is
		// the rule the backends had each reimplemented.
		var (pass, _, _) = SamplingPass();
		var descriptor = new TechniqueDescriptor { Shader = "mesh", Layout = VertexLayout.MeshStandard };
		var validator = ValidatorOn(pass, RegistryWith("mesh", descriptor, default));

		Should.NotThrow(() => validator.Draw(new DrawCall { Technique = "mesh", Mesh = new Mesh(1) }));
	}

	[Test]
	public void A_procedural_draw_with_no_vertex_count_and_no_instances_throws()
	{
		var (pass, _, _) = SamplingPass();
		var validator = ValidatorOn(pass, RegistryWith("particles", new TechniqueDescriptor { Shader = "particles" }, default));

		var ex = Should.Throw<InvalidDrawCallException>(() =>
			validator.Draw(new DrawCall { Technique = "particles" }));

		ex.Pass.ShouldBe("p");
		ex.Message.ShouldContain("VertexCount");
	}

	[Test]
	public void A_null_mesh_is_reported_with_the_pass_and_technique_even_with_validation_off()
	{
		// RequireMesh replaces a bare "Nullable object must have a value" at the deref, so it is not gated: turning
		// validation off must not turn a named error back into that.
		var (pass, _, _) = SamplingPass();
		var validator = ValidatorOn(pass, enabled: false);

		var ex = Should.Throw<InvalidDrawCallException>(() =>
			validator.RequireMesh(new DrawCall { Technique = "mesh", VertexCount = 3 }));

		ex.Message.ShouldContain("mesh");
		ex.Message.ShouldContain("p");
	}

	[Test]
	public void Validation_off_skips_the_declaration_scans()
	{
		var (pass, _, undeclared) = SamplingPass();
		var validator = ValidatorOn(pass, enabled: false);

		Should.NotThrow(() => validator.FullScreen("blit", [undeclared]));
	}

	[Test]
	public void A_wide_push_into_a_constant_buffer_that_declares_room_is_accepted()
	{
		// The regression guard for the correction that shaped this work: a push routed to a constant buffer is bounded
		// by that buffer's declared size, not by the 16-float scalar block. CascadedShadowsDemo pushes 68 floats and
		// the sky demos 36; a flat 16-float cap would break both on the first frame.
		var (pass, _, _) = SamplingPass();
		var shape = new TechniqueShape(default, [64, 272], ScalarPushBytes: 0, ListPushBytes: 0);
		var validator = ValidatorOn(pass, RegistryWith("csm", new TechniqueDescriptor { Shader = "csm" }, shape));

		Should.NotThrow(() => validator.PushFits("csm", 68 * sizeof(float), routesToUniform: true, uniformSlot: 1, scalarPushBytes: 0));
	}

	[Test]
	public void A_push_that_overflows_its_constant_buffer_throws()
	{
		var (pass, _, _) = SamplingPass();
		var shape = new TechniqueShape(default, [64, 272], ScalarPushBytes: 0, ListPushBytes: 0);
		var validator = ValidatorOn(pass, RegistryWith("csm", new TechniqueDescriptor { Shader = "csm" }, shape));

		var ex = Should.Throw<TechniqueMismatchException>(() =>
			validator.PushFits("csm", 69 * sizeof(float), routesToUniform: true, uniformSlot: 1, scalarPushBytes: 0));

		ex.Message.ShouldContain("69 floats");
		ex.Message.ShouldContain("272 bytes");
	}

	[Test]
	public void A_push_that_overflows_the_scalar_block_throws()
	{
		var (pass, _, _) = SamplingPass();
		var validator = ValidatorOn(pass);

		var ex = Should.Throw<TechniqueMismatchException>(() =>
			validator.PushFits("grade", 17 * sizeof(float), routesToUniform: false, 0, scalarPushBytes: 16 * sizeof(float)));

		ex.Message.ShouldContain("17 floats");
		ex.Message.ShouldContain("64 bytes");
	}

	[Test]
	public void A_push_with_no_channel_at_all_throws()
	{
		// The Vulkan silent-drop hole: the technique reflects constant buffers but the pass's .Reads filled them all,
		// and it declares no scalar block — so the bytes used to be discarded without a word.
		var (pass, _, _) = SamplingPass();
		var validator = ValidatorOn(pass);

		var ex = Should.Throw<TechniqueMismatchException>(() =>
			validator.PushFits("lit", 4 * sizeof(float), routesToUniform: false, 0, scalarPushBytes: 0));

		ex.Message.ShouldContain("LOOM_PUSH_CONSTANT");
	}

	[Test]
	public void A_push_fitting_the_scalar_block_exactly_is_accepted()
	{
		var (pass, _, _) = SamplingPass();
		var validator = ValidatorOn(pass);

		Should.NotThrow(() => validator.PushFits("grade", 16 * sizeof(float), routesToUniform: false, 0, 16 * sizeof(float)));
		Should.NotThrow(() => validator.PushFits("grade", 0, routesToUniform: false, 0, 0)); // an empty push needs no channel
	}

	[Test]
	public void An_unreflected_constant_buffer_falls_back_to_the_portable_bound_rather_than_rejecting()
	{
		// A reflected 0 means "size unknown", never "holds nothing". Treating it as zero capacity would reject every
		// push on a shader with no sidecar.
		var (pass, _, _) = SamplingPass();
		var validator = ValidatorOn(pass, RegistryWith("bare", new TechniqueDescriptor { Shader = "bare" }, default));

		Should.NotThrow(() => validator.PushFits("bare", 68 * sizeof(float), routesToUniform: true, 0, 0));
		Should.Throw<TechniqueMismatchException>(() =>
			validator.PushFits("bare", RecordValidator.UnknownUniformSlotBytes + sizeof(float), routesToUniform: true, 0, 0));
	}
}
