namespace BinaryVibrance.Kaldra.Core.Animation;

/// <summary>
/// One joint's animated transform within an <see cref="AnimationClip"/> — any of its translation,
/// rotation, and scale may be keyed. A null track means that component is not animated by this clip and the
/// joint's bind-pose value (<see cref="Skeleton.LocalBindTransforms"/>) is used instead. Backend-neutral, immutable.
/// </summary>
public sealed class JointChannel
{
	/// <summary>The index (into the <see cref="Skeleton"/>'s joint array) of the joint this channel drives.</summary>
	public required int JointIndex { get; init; }

	/// <summary>The joint's keyed translation, or null to use its bind-pose translation.</summary>
	public Vector3Track? Translation { get; init; }

	/// <summary>The joint's keyed rotation, or null to use its bind-pose rotation.</summary>
	public QuaternionTrack? Rotation { get; init; }

	/// <summary>The joint's keyed scale, or null to use its bind-pose scale.</summary>
	public Vector3Track? Scale { get; init; }
}
