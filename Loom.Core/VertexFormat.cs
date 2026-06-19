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
