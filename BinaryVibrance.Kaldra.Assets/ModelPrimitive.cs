using BinaryVibrance.Kaldra.Core.Animation;
using BinaryVibrance.Kaldra.Core.Geometry;

namespace BinaryVibrance.Kaldra.Assets;

/// <summary>One drawable primitive of a loaded model: geometry in Kaldra's canonical <see cref="Vertex"/> layout plus its material.</summary>
public sealed class ModelPrimitive
{
	/// <summary>Vertices in Kaldra's interleaved layout (position/normal/uv/tangent); tangents are taken from the
	/// glTF <c>TANGENT</c> accessor or computed when absent.</summary>
	public required Vertex[] Vertices { get; init; }

	/// <summary>Triangle-list indices.</summary>
	public required uint[] Indices { get; init; }

	/// <summary>The primitive's material (decoded textures), or null if it has none.</summary>
	public ModelMaterial? Material { get; init; }

	/// <summary>
	/// The same geometry in the skinned layout (<see cref="SkinnedVertex"/> — position/normal/uv/tangent + bone
	/// indices + weights) when the primitive's node carries a skin; null for a static primitive. The
	/// <see cref="Vertices"/> field always holds the bind-pose geometry too, so a consumer can ignore skinning.
	/// The bone indices reference the model's <see cref="LoadedModel.Skeleton"/> joint array.
	/// </summary>
	public SkinnedVertex[]? SkinnedVertices { get; init; }

	/// <summary>True when this primitive carries skinning data (<see cref="SkinnedVertices"/> is populated).</summary>
	public bool IsSkinned => SkinnedVertices is not null;
}
