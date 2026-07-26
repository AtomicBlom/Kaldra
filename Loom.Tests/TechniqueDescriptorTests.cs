using Loom.Core.Materials;

namespace Loom.Tests;

/// <summary>Describe-layer coverage for <see cref="TechniqueDescriptor"/>'s construction shorthands and defaults.</summary>
public class TechniqueDescriptorTests
{
	[Test]
	public void Compute_names_the_entry_as_the_shader_source_declares_it()
	{
		var technique = TechniqueDescriptor.Compute("skin");

		technique.Shader.ShouldBe("skin");
		technique.IsCompute.ShouldBeTrue();
		technique.ComputeEntry.ShouldBe("computeMain");
		technique.HasFragmentPush.ShouldBeFalse();
	}

	[Test]
	public void Compute_carries_its_entry_in_the_slot_the_backends_read()
	{
		// A compute technique has one entry point and it rides FragmentEntry; ComputeEntry is the alias that lets
		// backend code read the intent. Both must agree, or a backend reading the slot directly gets a stale name.
		var technique = TechniqueDescriptor.Compute("reduce", "sum", hasFragmentPush: true);

		technique.ComputeEntry.ShouldBe("sum");
		technique.FragmentEntry.ShouldBe("sum");
		technique.VertexEntry.ShouldBe("sum");
		technique.HasFragmentPush.ShouldBeTrue();
	}

	[Test]
	public void A_graphics_technique_defaults_to_the_conventional_stage_entries()
	{
		var technique = new TechniqueDescriptor("lit", HasVertexInput: true);

		technique.VertexEntry.ShouldBe("vertexMain");
		technique.FragmentEntry.ShouldBe("fragmentMain");
		technique.IsCompute.ShouldBeFalse();
	}
}
