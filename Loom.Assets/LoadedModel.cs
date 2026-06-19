namespace Loom.Assets;

/// <summary>A model flattened to drawable primitives — what an app turns into engine meshes/materials.</summary>
public sealed class LoadedModel
{
	/// <summary>The model's primitives, each ready to become a <c>Mesh</c> + <c>Material</c>.</summary>
	public required IReadOnlyList<ModelPrimitive> Primitives { get; init; }
}