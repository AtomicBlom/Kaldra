using Loom.Core.Geometry;
using Loom.Core.Hosting;
using Loom.Core.Materials;
using Loom.Graph;

namespace Loom.Tests;

/// <summary>
/// The neutral technique registry: resolving a technique by name, and the descriptor-derived facts the record-time
/// checks read off it. Device-free, which is the point — a technique-name miss and the "can this take a mesh?" rule
/// are the same on every backend, so they are settled here rather than three times behind a GPU.
/// </summary>
public class TechniqueRegistryTests
{
	private static TechniqueRegistry With(string name, TechniqueDescriptor descriptor)
	{
		var registry = new TechniqueRegistry();
		registry.Register(name, descriptor, default);
		return registry;
	}

	[Test]
	public void Requiring_an_unregistered_technique_throws_and_names_it()
	{
		var ex = Should.Throw<TechniqueNotDefinedException>(() => new TechniqueRegistry().Require("blit"));

		ex.Technique.ShouldBe("blit");
		ex.Message.ShouldContain("blit");
		ex.Message.ShouldContain("DefineTechnique");
		ex.ShouldBeAssignableTo<LoomAuthoringException>();
	}

	[Test]
	public void Finding_an_unregistered_technique_returns_null_rather_than_throwing()
	{
		// The record path uses Find where the backend's own lookup will report the miss with a better message, so
		// this must not pre-empt it.
		new TechniqueRegistry().Find("blit").ShouldBeNull();
	}

	[Test]
	public void A_registered_technique_round_trips()
	{
		var registry = With("mesh", new TechniqueDescriptor { Shader = "mesh", HasVertexInput = true });

		var record = registry.Require("mesh");
		record.Name.ShouldBe("mesh");
		record.Descriptor.Shader.ShouldBe("mesh");
	}

	[Test]
	public void Re_registering_a_name_replaces_it()
	{
		// A hot reload replays DefineTechnique, so the second registration must win rather than being ignored.
		var registry = With("mesh", new TechniqueDescriptor { Shader = "mesh", HasVertexInput = false });
		registry.Register("mesh", new TechniqueDescriptor { Shader = "mesh", HasVertexInput = true }, default);

		registry.Require("mesh").TakesVertices.ShouldBeTrue();
	}

	[Test]
	public void HasVertexInput_and_an_explicit_layout_both_mean_the_technique_takes_vertices()
	{
		// Two spellings of one fact. Both backends had reimplemented this rule separately; the record-time mesh check
		// reads it from here so it cannot disagree between them.
		With("a", new TechniqueDescriptor { Shader = "s", HasVertexInput = true }).Require("a").TakesVertices.ShouldBeTrue();
		With("b", new TechniqueDescriptor { Shader = "s", Layout = VertexLayout.MeshStandard }).Require("b").TakesVertices.ShouldBeTrue();
		With("c", new TechniqueDescriptor { Shader = "s" }).Require("c").TakesVertices.ShouldBeFalse();
	}

	[Test]
	public void An_instance_stream_alone_does_not_make_a_technique_take_vertices()
	{
		// The GPU-particle shape: a per-instance stream but no per-vertex data, so it fabricates its own verts and a
		// mesh handed to it would be ignored.
		var descriptor = new TechniqueDescriptor { Shader = "particle_render", Instance = VertexLayout.MeshStandard };
		var record = With("particles", descriptor).Require("particles");

		record.TakesVertices.ShouldBeFalse();
		record.TakesInstances.ShouldBeTrue();
	}

	[Test]
	public void The_shape_carries_the_reflected_uniform_slot_sizes()
	{
		var registry = new TechniqueRegistry();
		registry.Register("csm", new TechniqueDescriptor("csm"), new TechniqueShape(default, [64, 272], 0, 0));

		var shape = registry.Require("csm").Shape;
		shape.UniformSlotBytes(0).ShouldBe(64);
		shape.UniformSlotBytes(1).ShouldBe(272);
		shape.UniformSlotBytes(2).ShouldBe(0);  // absent reads as unknown, not as an exception
		default(TechniqueShape).UniformSlotBytes(0).ShouldBe(0);
	}
}
