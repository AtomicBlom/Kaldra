using BinaryVibrance.Kaldra.Core.Animation;
using BinaryVibrance.Kaldra.Core.Geometry;

namespace BinaryVibrance.Kaldra.Tests;

/// <summary>The describe-layer vertex-layout vocabulary: packed offsets, stride, and value equality.</summary>
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
		layout.Stride.ShouldBe(48); // Vector3 + Vector3 + Vector2 + Vector4 — the same 48 bytes as BinaryVibrance.Kaldra.Core.Vertex
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

	[Test]
	[Arguments(VertexFormat.UInt4, 16)]
	[Arguments(VertexFormat.Half4, 8)]
	[Arguments(VertexFormat.UNorm4, 4)]
	public void Wide_format_byte_sizes(VertexFormat format, int expected) =>
		format.ByteSize().ShouldBe(expected);

	[Test]
	public void Overlay_layout_matches_the_overlay_vertex()
	{
		var layout = VertexLayout.Overlay;

		layout.InputRate.ShouldBe(VertexInputRate.PerVertex);
		layout.Binding.ShouldBe(0);
		// Position (Float2) + Uv (Float2) + premultiplied colour (Half4) + per-vertex Custom (Float4) — the overlay vertex.
		layout.Stride.ShouldBe(40);
		layout.Offsets.ToArray().ShouldBe(new[] { 0, 8, 16, 24 });
		layout.Attributes.Select(a => a.Semantic).ToArray().ShouldBe(new[] { "POSITION", "TEXCOORD", "COLOR", "TEXCOORD" });
		layout.Attributes.Select(a => a.Format).ToArray().ShouldBe(new[] { VertexFormat.Float2, VertexFormat.Float2, VertexFormat.Half4, VertexFormat.Float4 });
	}

	[Test]
	public void Overlay_vertex_struct_size_matches_the_layout() =>
		System.Runtime.CompilerServices.Unsafe.SizeOf<OverlayVertex>().ShouldBe(VertexLayout.Overlay.Stride);

	[Test]
	public void Mesh_skinned_extends_the_standard_layout_with_joints_and_weights()
	{
		var layout = VertexLayout.MeshSkinned;

		layout.InputRate.ShouldBe(VertexInputRate.PerVertex);
		layout.Binding.ShouldBe(0);
		layout.Stride.ShouldBe(80); // MeshStandard (48) + JOINTS (UInt4, 16) + WEIGHTS (Float4, 16) — matches SkinnedVertex
		layout.Offsets.ToArray().ShouldBe(new[] { 0, 12, 24, 32, 48, 64 });
		layout.Attributes.Select(a => a.Semantic).ToArray().ShouldBe(new[] { "POSITION", "NORMAL", "TEXCOORD", "TANGENT", "JOINTS", "WEIGHTS" });
		layout.Attributes[4].Format.ShouldBe(VertexFormat.UInt4);
	}

	[Test]
	public void Skinned_vertex_struct_is_80_bytes_matching_the_layout() =>
		System.Runtime.CompilerServices.Unsafe.SizeOf<SkinnedVertex>().ShouldBe(VertexLayout.MeshSkinned.Stride);
}
