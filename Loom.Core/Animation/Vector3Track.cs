using System.Collections.Immutable;
using System.Numerics;

namespace Loom.Core.Animation;

/// <summary>
/// A keyframed <see cref="Vector3"/> animation channel — a joint's translation or scale over time (M15). The
/// <see cref="Times"/> are strictly increasing seconds; <see cref="Values"/>[i] is the value at <c>Times[i]</c>.
/// The two arrays have equal length (≥ 1). Backend-neutral, immutable.
/// </summary>
public sealed class Vector3Track
{
	/// <summary>Strictly increasing keyframe times, in seconds.</summary>
	public required ImmutableArray<float> Times { get; init; }

	/// <summary>The value at each keyframe — same length as <see cref="Times"/>.</summary>
	public required ImmutableArray<Vector3> Values { get; init; }

	/// <summary>How to interpolate between keyframes.</summary>
	public required AnimationInterpolation Interpolation { get; init; }
}
