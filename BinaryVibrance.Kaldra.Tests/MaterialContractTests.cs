using BinaryVibrance.Kaldra.Core.Hosting;
using BinaryVibrance.Kaldra.Core.Materials;
using BinaryVibrance.Kaldra.Graph;

namespace BinaryVibrance.Kaldra.Tests;

/// <summary>
/// The material/technique contract: what a technique expects of a material (from its shader's reflection), when the
/// disagreement is reported, and that it is reported the same way whichever backend is drawing. This is the shared
/// half — <see cref="MaterialStore"/> is what all three backends call, and it raises the neutral
/// <see cref="TechniqueMismatchException"/> so an app handles the mismatch without knowing which backend drew.
/// </summary>
public class MaterialContractTests
{
	// Only the id reaches MaterialStore; the dimensions are the backend's business.
	private static Texture Tex(int id) => new(id, 1, 1);

	[Test]
	public void A_material_with_enough_textures_drives_the_technique()
	{
		var store = new MaterialStore();
		store.RegisterTechnique("gbuffer", new MaterialLayout(TextureCount: 2, ParameterByteSize: 0));
		var material = store.Create("gbuffer", [Tex(1), Tex(2)], default);

		Should.NotThrow(() => store.ValidateAgainst(material, "gbuffer"));
	}

	[Test]
	public void Extra_textures_are_allowed()
	{
		// One material feeding both a G-buffer pass and a depth-only pass that reflects no samplers is the reason
		// this direction is legal — the technique picks the pipeline, so it takes what it needs and ignores the rest.
		var store = new MaterialStore();
		store.RegisterTechnique("depthonly", new MaterialLayout(0, 0));
		var material = store.Create("depthonly", [Tex(1), Tex(2), Tex(3)], default);

		Should.NotThrow(() => store.ValidateAgainst(material, "depthonly"));
	}

	[Test]
	public void Too_few_textures_is_rejected_at_create_when_the_technique_is_already_defined()
	{
		var store = new MaterialStore();
		store.RegisterTechnique("gbuffer", new MaterialLayout(3, 0));

		var ex = Should.Throw<TechniqueMismatchException>(() => store.Create("gbuffer", [Tex(1)], default));
		ex.Message.ShouldContain("gbuffer");
		ex.Message.ShouldContain("1 texture");
		ex.Message.ShouldContain("3");
	}

	[Test]
	public void Too_few_textures_is_rejected_at_define_when_the_material_came_first()
	{
		// The order the app happens to do these in must not decide whether the mismatch is caught.
		var store = new MaterialStore();
		store.Create("gbuffer", [Tex(1)], default);

		Should.Throw<TechniqueMismatchException>(() => store.RegisterTechnique("gbuffer", new MaterialLayout(3, 0)))
			.Message.ShouldContain("gbuffer");
	}

	[Test]
	public void Redefining_a_technique_with_a_new_sampler_count_revalidates_existing_materials()
	{
		// The headline of #22: DefineTechnique is replayed on hot reload, so adding a sampler to a shader is caught
		// at the reload against every material already built for it — instead of silently sampling the wrong texture.
		var store = new MaterialStore();
		store.RegisterTechnique("gbuffer", new MaterialLayout(2, 0));
		store.Create("gbuffer", [Tex(1), Tex(2)], default);

		var ex = Should.Throw<TechniqueMismatchException>(() => store.RegisterTechnique("gbuffer", new MaterialLayout(3, 0)));
		ex.Message.ShouldContain("2 texture");
		ex.Message.ShouldContain("3");
	}

	[Test]
	public void Redefining_a_technique_unchanged_revalidates_nothing()
	{
		var store = new MaterialStore();
		store.RegisterTechnique("gbuffer", new MaterialLayout(2, 0));
		store.Create("gbuffer", [Tex(1), Tex(2)], default);

		Should.NotThrow(() => store.RegisterTechnique("gbuffer", new MaterialLayout(2, 0)));
	}

	[Test]
	public void A_material_built_without_a_technique_is_still_checked_at_draw_time()
	{
		// The untyped CreateMaterial(textures) records no pairing, so nothing can be checked up front — but the draw
		// path still validates against whatever technique actually draws it.
		var store = new MaterialStore();
		store.RegisterTechnique("gbuffer", new MaterialLayout(2, 0));
		var material = store.Create([Tex(1)]);

		Should.Throw<TechniqueMismatchException>(() => store.ValidateAgainst(material, "gbuffer"));
	}

	[Test]
	public void A_technique_with_no_reflection_accepts_any_material()
	{
		// A technique registered with a default layout claims no textures and no parameters. That must read as
		// "nothing to check", not as "must supply zero textures".
		var store = new MaterialStore();
		var material = store.Create([Tex(1), Tex(2)]);

		Should.NotThrow(() => store.ValidateAgainst(material, "never-registered"));
		store.RegisterTechnique("builtin", default);
		Should.NotThrow(() => store.ValidateAgainst(material, "builtin"));
	}

	[Test]
	public void A_parameter_block_the_technique_does_not_declare_is_rejected()
	{
		// The property that keeps CreateMaterial<T> from silently no-op'ing: parameters can only be attached to a
		// technique whose shader actually declares a kaldraMaterial block, so bytes are never quietly dropped.
		var store = new MaterialStore();
		store.RegisterTechnique("lit", new MaterialLayout(1, ParameterByteSize: 0));

		Should.Throw<TechniqueMismatchException>(() => store.Create("lit", [Tex(1)], new byte[48]))
			.Message.ShouldContain("48-byte");
	}

	[Test]
	public void A_missing_parameter_block_is_rejected_and_says_how_to_supply_one()
	{
		var store = new MaterialStore();
		store.RegisterTechnique("pbr", new MaterialLayout(4, 48));

		var ex = Should.Throw<TechniqueMismatchException>(() => store.Create("pbr", [Tex(1), Tex(2), Tex(3), Tex(4)], default));
		ex.Message.ShouldContain("kaldraMaterial");
		ex.Message.ShouldContain("CreateMaterial<T>");
	}

	[Test]
	public void A_wrong_sized_parameter_block_is_rejected()
	{
		// Exact match, both directions: a short block lets the shader read past it, a long one means the two
		// disagree about the struct.
		var store = new MaterialStore();
		store.RegisterTechnique("pbr", new MaterialLayout(0, 48));

		Should.Throw<TechniqueMismatchException>(() => store.Create("pbr", [], new byte[32]));
		Should.Throw<TechniqueMismatchException>(() => store.Create("pbr", [], new byte[64]));
		Should.NotThrow(() => store.Create("pbr", [], new byte[48]));
	}

	[Test]
	public void Parameters_round_trip_and_can_be_rewritten_in_place()
	{
		var store = new MaterialStore();
		store.RegisterTechnique("pbr", new MaterialLayout(0, 4));
		var material = store.Create("pbr", [], new byte[] { 1, 2, 3, 4 });

		store.ParametersOf(material.Id).ToArray().ShouldBe([(byte)1, (byte)2, (byte)3, (byte)4]);

		store.WriteParameters(material.Id, new byte[] { 9, 8, 7, 6 });
		store.ParametersOf(material.Id).ToArray().ShouldBe([(byte)9, (byte)8, (byte)7, (byte)6]);
	}

	[Test]
	public void Writing_a_differently_sized_block_is_rejected()
	{
		var store = new MaterialStore();
		store.RegisterTechnique("pbr", new MaterialLayout(0, 4));
		var material = store.Create("pbr", [], new byte[4]);

		Should.Throw<InvalidOperationException>(() => store.WriteParameters(material.Id, new byte[8]));
	}

	[Test]
	public void Writing_parameters_to_a_material_that_has_none_says_how_to_give_it_some()
	{
		var store = new MaterialStore();
		var material = store.Create([Tex(1)]);

		Should.Throw<InvalidOperationException>(() => store.WriteParameters(material.Id, new byte[16]))
			.Message.ShouldContain("CreateMaterial<T>");
	}

	[Test]
	public void The_layout_of_an_unregistered_technique_is_null()
	{
		var store = new MaterialStore();

		store.LayoutOf("nope").ShouldBeNull();
		store.RegisterTechnique("yep", new MaterialLayout(2, 16));
		store.LayoutOf("yep").ShouldBe(new MaterialLayout(2, 16));
	}

	[Test]
	public void A_technique_that_compares_accepts_no_material_at_creation_or_at_draw()
	{
		// A material's textures are colour formats, which no backend can compare, so the same refusal on every backend
		var store = new MaterialStore();
		store.RegisterTechnique("shadowed", new MaterialLayout(TextureCount: 1, ParameterByteSize: 0, ComparedTextureCount: 1));

		Should.Throw<TechniqueMismatchException>(() => store.Create("shadowed", [new Texture(1, 1, 1)], default))
			.Message.ShouldContain("by comparison");
		var untyped = store.Create([new Texture(1, 1, 1)]);
		Should.Throw<TechniqueMismatchException>(() => store.ValidateAgainst(untyped, "shadowed"))
			.Message.ShouldContain(".Samples");
	}
}
