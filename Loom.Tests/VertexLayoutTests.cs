using Loom.Core;

namespace Loom.Tests;

/// <summary>The describe-layer vertex-layout vocabulary (M12): packed offsets, stride, and value equality.</summary>
public class VertexLayoutTests
{
	[Theory]
	[InlineData(VertexFormat.Float, 4)]
	[InlineData(VertexFormat.Float2, 8)]
	[InlineData(VertexFormat.Float3, 12)]
	[InlineData(VertexFormat.Float4, 16)]
	public void Vertex_format_byte_sizes(VertexFormat format, int expected) =>
		Assert.Equal(expected, format.ByteSize());

	[Fact]
	public void Mesh_standard_matches_the_canonical_Vertex_layout()
	{
		var layout = VertexLayout.MeshStandard;

		Assert.Equal(VertexInputRate.PerVertex, layout.InputRate);
		Assert.Equal(0, layout.Binding);
		Assert.Equal(32, layout.Stride); // Vector3 + Vector3 + Vector2 — the same 32 bytes as Loom.Core.Vertex
		Assert.Equal(new[] { 0, 12, 24 }, layout.Offsets.ToArray());
		Assert.Equal(new[] { "POSITION", "NORMAL", "TEXCOORD" }, layout.Attributes.Select(a => a.Semantic).ToArray());
	}

	[Fact]
	public void Transform_is_a_per_instance_matrix_at_binding_one()
	{
		var layout = VertexLayout.Transform;

		Assert.Equal(VertexInputRate.PerInstance, layout.InputRate);
		Assert.Equal(1, layout.Binding);
		Assert.Equal(64, layout.Stride); // a 4x4 matrix as four float4 rows
		Assert.Equal(new[] { 0, 16, 32, 48 }, layout.Offsets.ToArray());
		Assert.All(layout.Attributes, a => Assert.Equal("ITRANSFORM", a.Semantic));
	}

	[Fact]
	public void Offsets_pack_attributes_tightly_in_declaration_order()
	{
		var layout = new VertexLayout(
			new VertexAttribute("POSITION", VertexFormat.Float3),
			new VertexAttribute("TANGENT", VertexFormat.Float4),
			new VertexAttribute("TEXCOORD", VertexFormat.Float2));

		Assert.Equal(new[] { 0, 12, 28 }, layout.Offsets.ToArray()); // 12, then +16, then +8
		Assert.Equal(36, layout.Stride);
	}

	[Fact]
	public void Layouts_have_value_equality_over_attributes_rate_and_binding()
	{
		var a = new VertexLayout(new VertexAttribute("POSITION", VertexFormat.Float3));
		var b = new VertexLayout(new VertexAttribute("POSITION", VertexFormat.Float3));
		var different = new VertexLayout(new VertexAttribute("POSITION", VertexFormat.Float2));

		Assert.Equal(a, b);
		Assert.Equal(a.GetHashCode(), b.GetHashCode());
		Assert.NotEqual(a, different);
	}

	[Fact]
	public void An_empty_layout_is_rejected() =>
		Assert.Throws<ArgumentException>(() => new VertexLayout());
}
