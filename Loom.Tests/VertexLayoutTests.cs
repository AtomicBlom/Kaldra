using Loom.Core;

namespace Loom.Tests;

/// <summary>The describe-layer vertex-layout vocabulary (M12): packed offsets, stride, and value equality.</summary>
public class VertexLayoutTests
{
	[Test]
	[Arguments(VertexFormat.Float, 4)]
	[Arguments(VertexFormat.Float2, 8)]
	[Arguments(VertexFormat.Float3, 12)]
	[Arguments(VertexFormat.Float4, 16)]
	public void Vertex_format_byte_sizes(VertexFormat format, int expected) =>
		format.ByteSize().ShouldBe(expected);

	[Test]
	public void Mesh_standard_matches_the_canonical_Vertex_layout()
	{
		var layout = VertexLayout.MeshStandard;

		layout.InputRate.ShouldBe(VertexInputRate.PerVertex);
		layout.Binding.ShouldBe(0);
		layout.Stride.ShouldBe(48); // Vector3 + Vector3 + Vector2 + Vector4 — the same 48 bytes as Loom.Core.Vertex (M11 added the tangent)
		layout.Offsets.ToArray().ShouldBe(new[] { 0, 12, 24, 32 });
		layout.Attributes.Select(a => a.Semantic).ToArray().ShouldBe(new[] { "POSITION", "NORMAL", "TEXCOORD", "TANGENT" });
	}

	[Test]
	public void Transform_is_a_per_instance_matrix_at_binding_one()
	{
		var layout = VertexLayout.Transform;

		layout.InputRate.ShouldBe(VertexInputRate.PerInstance);
		layout.Binding.ShouldBe(1);
		layout.Stride.ShouldBe(64); // a 4x4 matrix as four float4 rows
		layout.Offsets.ToArray().ShouldBe(new[] { 0, 16, 32, 48 });
		layout.Attributes.ShouldAllBe(a => a.Semantic == "ITRANSFORM");
	}

	[Test]
	public void Offsets_pack_attributes_tightly_in_declaration_order()
	{
		var layout = new VertexLayout(
			new VertexAttribute("POSITION", VertexFormat.Float3),
			new VertexAttribute("TANGENT", VertexFormat.Float4),
			new VertexAttribute("TEXCOORD", VertexFormat.Float2));

		layout.Offsets.ToArray().ShouldBe(new[] { 0, 12, 28 }); // 12, then +16, then +8
		layout.Stride.ShouldBe(36);
	}

	[Test]
	public void Layouts_have_value_equality_over_attributes_rate_and_binding()
	{
		var a = new VertexLayout(new VertexAttribute("POSITION", VertexFormat.Float3));
		var b = new VertexLayout(new VertexAttribute("POSITION", VertexFormat.Float3));
		var different = new VertexLayout(new VertexAttribute("POSITION", VertexFormat.Float2));

		b.ShouldBe(a);
		b.GetHashCode().ShouldBe(a.GetHashCode());
		different.ShouldNotBe(a);
	}

	[Test]
	public void An_empty_layout_is_rejected() =>
		Should.Throw<ArgumentException>(() => new VertexLayout());
}
