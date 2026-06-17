namespace Loom.Core;

/// <summary>
/// Backend-neutral vertex-attribute formats — the element type of one attribute in a <see cref="VertexLayout"/>.
/// Deliberately separate from <see cref="Format"/> (which is render-target / texture oriented and has no
/// three-component float concept): a vertex attribute is just a small tuple of 32-bit floats today. The
/// backend maps each to its concrete API format (e.g. <see cref="Float3"/> → <c>VK_FORMAT_R32G32B32_SFLOAT</c> /
/// <c>DXGI_FORMAT_R32G32B32_FLOAT</c>). Grow this deliberately as layouts need more (packed colours, integer
/// bone indices for skinning in M15).
/// </summary>
public enum VertexFormat
{
	/// <summary>One 32-bit float.</summary>
	Float,

	/// <summary>Two 32-bit floats (e.g. a UV).</summary>
	Float2,

	/// <summary>Three 32-bit floats (e.g. a position or normal).</summary>
	Float3,

	/// <summary>Four 32-bit floats (e.g. a tangent, a colour, or one row of an instance transform).</summary>
	Float4,
}

/// <summary>Helpers over <see cref="VertexFormat"/> — kept here so the enum stays a plain value list.</summary>
public static class VertexFormatExtensions
{
	/// <summary>The size of one attribute of this format, in bytes — drives the packed offsets/stride of a <see cref="VertexLayout"/>.</summary>
	public static int ByteSize(this VertexFormat format) => format switch
	{
		VertexFormat.Float => sizeof(float),
		VertexFormat.Float2 => 2 * sizeof(float),
		VertexFormat.Float3 => 3 * sizeof(float),
		VertexFormat.Float4 => 4 * sizeof(float),
		_ => throw new ArgumentOutOfRangeException(nameof(format), format, "Unknown vertex format."),
	};

	/// <summary>The number of 32-bit components — useful for the backends' attribute-description builders.</summary>
	public static int ComponentCount(this VertexFormat format) => format switch
	{
		VertexFormat.Float => 1,
		VertexFormat.Float2 => 2,
		VertexFormat.Float3 => 3,
		VertexFormat.Float4 => 4,
		_ => throw new ArgumentOutOfRangeException(nameof(format), format, "Unknown vertex format."),
	};
}
