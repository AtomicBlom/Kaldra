using System.Collections.Immutable;
using System.Numerics;

namespace BinaryVibrance.Kaldra.Core.Animation;

/// <summary>
/// A keyframed <see cref="Quaternion"/> animation channel — a joint's rotation over time. The
/// <see cref="Times"/> are strictly increasing seconds; <see cref="Values"/>[i] is the (unit) rotation at
/// <c>Times[i]</c>. Linear interpolation slerps between the surrounding keyframes. Backend-neutral, immutable.
/// </summary>
public sealed class QuaternionTrack
{
	/// <summary>Strictly increasing keyframe times, in seconds.</summary>
	public required ImmutableArray<float> Times { get; init; }

	/// <summary>The (unit) rotation at each keyframe — same length as <see cref="Times"/>.</summary>
	public required ImmutableArray<Quaternion> Values { get; init; }

	/// <summary>How to interpolate between keyframes.</summary>
	public required AnimationInterpolation Interpolation { get; init; }
}
