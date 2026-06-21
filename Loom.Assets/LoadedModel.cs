using Loom.Core.Animation;

namespace Loom.Assets;

/// <summary>A model flattened to drawable primitives — what an app turns into engine meshes/materials.</summary>
public sealed class LoadedModel
{
	/// <summary>The model's primitives, each ready to become a <c>Mesh</c> + <c>Material</c>.</summary>
	public required IReadOnlyList<ModelPrimitive> Primitives { get; init; }

	/// <summary>
	/// The model's skeleton (M15), or null if it is not rigged. Shared by every skinned primitive; the
	/// <see cref="ModelPrimitive.SkinnedVertices"/> bone indices reference its joint array. Loom reads the first
	/// glTF skin (a single-character model has exactly one).
	/// </summary>
	public Skeleton? Skeleton { get; init; }

	/// <summary>The model's animation clips (M15), in glTF order; empty when unrigged or unanimated.</summary>
	public IReadOnlyList<AnimationClip> Clips { get; init; } = [];
}
