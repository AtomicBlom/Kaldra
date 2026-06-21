using System.Collections.Immutable;
using System.Numerics;

namespace Loom.Core.Animation;

/// <summary>
/// A mesh's skeleton (M15): a flat joint array (in glTF skin order) where each joint records its parent index
/// (<c>-1</c> for a root), its inverse bind matrix, and its bind-pose local transform. The parents form a
/// forest and may appear in any order — the animation runtime resolves world matrices accordingly, so no
/// topological sort is assumed. Backend-neutral (<c>System.Numerics</c>), immutable — the natural output of a glTF skin.
/// <para>
/// The animation runtime composes a clip's sampled local transforms down this hierarchy into joint world
/// matrices, then multiplies each by its <see cref="InverseBindMatrices"/> entry to get the bone palette the
/// skinning shader applies. A <see cref="SkinnedVertex"/>'s joint indices reference this array (and thus the
/// palette slots). <see cref="LocalBindTransforms"/> is the fallback when a clip leaves a joint (or a joint's
/// translation/rotation/scale) un-keyed.
/// </para>
/// </summary>
public sealed class Skeleton
{
	/// <summary>Each joint's parent index (into this same array), or <c>-1</c> for a root joint. Any order.</summary>
	public required ImmutableArray<int> ParentIndices { get; init; }

	/// <summary>Each joint's inverse bind matrix — transforms a vertex from model space into the joint's local space at bind pose.</summary>
	public required ImmutableArray<Matrix4x4> InverseBindMatrices { get; init; }

	/// <summary>Each joint's bind-pose local transform (relative to its parent) — the fallback pose for un-keyed channels.</summary>
	public required ImmutableArray<Matrix4x4> LocalBindTransforms { get; init; }

	/// <summary>Each joint's name (from glTF), for the skeleton debug viewer and clip retargeting. May be empty.</summary>
	public ImmutableArray<string> JointNames { get; init; } = [];

	/// <summary>The number of joints (the length of the per-joint arrays, and the bone palette's size).</summary>
	public int JointCount => ParentIndices.Length;
}
