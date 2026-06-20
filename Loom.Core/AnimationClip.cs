using System.Collections.Immutable;

namespace Loom.Core;

/// <summary>
/// A named skeletal animation (M15) — a set of per-joint <see cref="JointChannel"/>s plus the clip's total
/// <see cref="Duration"/>. Sampled at a time in <c>[0, Duration]</c> (the app loops/clamps as it likes) to
/// produce per-joint local transforms, which the animation runtime composes down the skeleton into the bone
/// palette. Backend-neutral, immutable; the natural output of a glTF animation. One clip drives one skeleton.
/// </summary>
public sealed class AnimationClip
{
	/// <summary>The clip's name (from the glTF animation), for selection/debug UIs.</summary>
	public required string Name { get; init; }

	/// <summary>The clip length in seconds — the maximum keyframe time across its channels.</summary>
	public required float Duration { get; init; }

	/// <summary>The animated joints. A joint absent here keeps its bind pose throughout the clip.</summary>
	public required ImmutableArray<JointChannel> Channels { get; init; }
}
