using BinaryVibrance.Kaldra.Core.Materials;

namespace BinaryVibrance.Kaldra.Graph;

/// <summary>
/// A registered technique: the name it was defined under, the descriptor the app declared, and the
/// <see cref="TechniqueShape"/> the backend baked from its shader. A class rather than a struct so a registry lookup
/// on the per-draw path hands back a reference instead of copying.
/// </summary>
public sealed class TechniqueRecord(string name, TechniqueDescriptor descriptor, TechniqueShape shape)
{
	/// <summary>The logical name the technique was defined under.</summary>
	public string Name { get; } = name;

	/// <summary>The descriptor the app declared — pipeline intent, not resource counts.</summary>
	public TechniqueDescriptor Descriptor { get; } = descriptor;

	/// <summary>What the backend baked from the technique's shader.</summary>
	public TechniqueShape Shape { get; } = shape;

	/// <summary>
	/// Whether the technique reads per-vertex data, and so can be handed a mesh. A technique that declares neither an
	/// explicit layout nor <c>HasVertexInput</c> fabricates its own vertices from the vertex index, so binding a mesh
	/// to it is meaningless — the mesh would be ignored and the draw would silently render the wrong geometry.
	/// <para>
	/// Derived here rather than read off a backend's baked pipeline because not every backend keeps the flag, and the
	/// two that do had reimplemented this same rule separately.
	/// </para>
	/// </summary>
	public bool TakesVertices => Descriptor.HasVertexInput || Descriptor.Layout is not null;

	/// <summary>Whether the technique declares a per-instance stream, and so can be handed an instance source.</summary>
	public bool TakesInstances => Descriptor.Instance is not null;
}
