using Loom.Core;

namespace Loom.Assets;

/// <summary>One drawable primitive of a loaded model: geometry in Loom's canonical <see cref="Vertex"/> layout plus its material.</summary>
public sealed class ModelPrimitive
{
	/// <summary>Vertices in Loom's interleaved layout (position/normal/uv/tangent); tangents are taken from the
	/// glTF <c>TANGENT</c> accessor or computed when absent.</summary>
	public required Vertex[] Vertices { get; init; }

	/// <summary>Triangle-list indices.</summary>
	public required uint[] Indices { get; init; }

	/// <summary>The primitive's material (decoded textures), or null if it has none.</summary>
	public ModelMaterial? Material { get; init; }
}
