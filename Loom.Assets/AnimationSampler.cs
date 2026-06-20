using System.Numerics;

using Loom.Core;

namespace Loom.Assets;

/// <summary>
/// The neutral, GC-free skeletal animation runtime (M15): samples an <see cref="AnimationClip"/> at a time into
/// per-joint local transforms, composes them down the <see cref="Skeleton"/> hierarchy into joint world
/// matrices, and multiplies by the inverse bind matrices to produce the <em>bone palette</em> the GPU skinning
/// shader applies (<c>palette[j] = inverseBind[j] · jointWorld[j]</c>, row-vector / <c>System.Numerics</c>
/// convention, so a model-space vertex skins as <c>v · Σ wₖ·palette[jₖ]</c>). Pure math over the neutral Core
/// types — it belongs with the producer (the loader), not in Core, and the app calls it per frame and hands the
/// palette to <c>ctx.DispatchSkin</c> (pulled, not captured — §8).
/// </summary>
public static class AnimationSampler
{
	/// <summary>
	/// Sample <paramref name="clip"/> at <paramref name="timeSeconds"/> and write <paramref name="skeleton"/>'s
	/// bone palette into <paramref name="palette"/> (length ≥ <see cref="Skeleton.JointCount"/>). The clip's
	/// channels override the joints' bind-pose local transforms; un-keyed joints (or un-keyed T/R/S components)
	/// keep their bind pose. Resolves the hierarchy for any parent order (memoized world composition).
	/// </summary>
	public static void Evaluate(Skeleton skeleton, AnimationClip clip, float timeSeconds, Span<Matrix4x4> palette)
	{
		var count = skeleton.JointCount;
		if (palette.Length < count)
			throw new ArgumentException($"Palette span holds {palette.Length} matrices but the skeleton has {count} joints.", nameof(palette));

		// Per-joint local transform: start from the bind-pose local (decomposed so an un-keyed component survives),
		// then override whichever of T/R/S the clip animates for that joint.
		Span<Matrix4x4> local = count <= 128 ? stackalloc Matrix4x4[count] : new Matrix4x4[count];
		for (var j = 0; j < count; j++)
			local[j] = skeleton.LocalBindTransforms[j];

		foreach (var channel in clip.Channels)
		{
			var j = channel.JointIndex;
			if ((uint)j >= (uint)count)
				continue;

			// Decompose the bind-pose local for the fallback components; recompose with the sampled overrides.
			if (!Matrix4x4.Decompose(skeleton.LocalBindTransforms[j], out var bindScale, out var bindRotation, out var bindTranslation))
			{
				bindScale = Vector3.One;
				bindRotation = Quaternion.Identity;
				bindTranslation = Vector3.Zero;
			}

			var translation = channel.Translation is { } t ? SampleVector3(t, timeSeconds) : bindTranslation;
			var rotation = channel.Rotation is { } r ? SampleQuaternion(r, timeSeconds) : bindRotation;
			var scale = channel.Scale is { } s ? SampleVector3(s, timeSeconds) : bindScale;

			// Row-vector TRS (matches glTF / SharpGLTF Node.LocalMatrix): scale, then rotate, then translate.
			local[j] = Matrix4x4.CreateScale(scale)
				* Matrix4x4.CreateFromQuaternion(rotation)
				* Matrix4x4.CreateTranslation(translation);
		}

		// World composition, robust to any parent order: world[j] = local[j] · world[parent[j]] (row-vector),
		// resolved lazily so a parent that appears after its child is still computed first. Skeletons are shallow.
		Span<Matrix4x4> world = count <= 128 ? stackalloc Matrix4x4[count] : new Matrix4x4[count];
		Span<bool> resolved = count <= 128 ? stackalloc bool[count] : new bool[count];
		for (var j = 0; j < count; j++)
			ResolveWorld(j, skeleton.ParentIndices.AsSpan(), local, world, resolved);

		for (var j = 0; j < count; j++)
			palette[j] = skeleton.InverseBindMatrices[j] * world[j];
	}

	/// <summary>
	/// Sample <paramref name="clip"/> at <paramref name="timeSeconds"/> and write each joint's WORLD matrix into
	/// <paramref name="world"/> (length ≥ <see cref="Skeleton.JointCount"/>) — the joint origins/axes the skeleton
	/// debug viewer draws (M15-7), as opposed to the inverse-bind-folded palette <see cref="Evaluate"/> produces.
	/// </summary>
	public static void EvaluateJointWorld(Skeleton skeleton, AnimationClip clip, float timeSeconds, Span<Matrix4x4> world)
	{
		var count = skeleton.JointCount;
		if (world.Length < count)
			throw new ArgumentException($"World span holds {world.Length} matrices but the skeleton has {count} joints.", nameof(world));

		Span<Matrix4x4> local = count <= 128 ? stackalloc Matrix4x4[count] : new Matrix4x4[count];
		for (var j = 0; j < count; j++)
			local[j] = skeleton.LocalBindTransforms[j];
		foreach (var channel in clip.Channels)
		{
			var j = channel.JointIndex;
			if ((uint)j >= (uint)count)
				continue;
			if (!Matrix4x4.Decompose(skeleton.LocalBindTransforms[j], out var bindScale, out var bindRotation, out var bindTranslation))
			{
				bindScale = Vector3.One;
				bindRotation = Quaternion.Identity;
				bindTranslation = Vector3.Zero;
			}
			var translation = channel.Translation is { } t ? SampleVector3(t, timeSeconds) : bindTranslation;
			var rotation = channel.Rotation is { } r ? SampleQuaternion(r, timeSeconds) : bindRotation;
			var scale = channel.Scale is { } s ? SampleVector3(s, timeSeconds) : bindScale;
			local[j] = Matrix4x4.CreateScale(scale) * Matrix4x4.CreateFromQuaternion(rotation) * Matrix4x4.CreateTranslation(translation);
		}

		Span<bool> resolved = count <= 128 ? stackalloc bool[count] : new bool[count];
		for (var j = 0; j < count; j++)
			world[j] = ResolveWorld(j, skeleton.ParentIndices.AsSpan(), local, world, resolved);
	}

	private static Matrix4x4 ResolveWorld(int j, ReadOnlySpan<int> parents, ReadOnlySpan<Matrix4x4> local, Span<Matrix4x4> world, Span<bool> resolved)
	{
		if (resolved[j])
			return world[j];
		var parent = parents[j];
		world[j] = parent >= 0 ? local[j] * ResolveWorld(parent, parents, local, world, resolved) : local[j];
		resolved[j] = true;
		return world[j];
	}

	// Sample a Vector3 track: clamp outside the key range, else lerp (or hold, for Step) within the segment.
	private static Vector3 SampleVector3(Vector3Track track, float time)
	{
		var (i, frac, step) = Locate(track.Times.AsSpan(), time, track.Interpolation);
		if (frac <= 0f || step)
			return track.Values[i];
		return Vector3.Lerp(track.Values[i], track.Values[i + 1], frac);
	}

	// Sample a Quaternion track: clamp outside the key range, else slerp (or hold, for Step) within the segment.
	private static Quaternion SampleQuaternion(QuaternionTrack track, float time)
	{
		var (i, frac, step) = Locate(track.Times.AsSpan(), time, track.Interpolation);
		if (frac <= 0f || step)
			return track.Values[i];
		return Quaternion.Slerp(track.Values[i], track.Values[i + 1], frac);
	}

	// Find the keyframe segment containing <paramref name="time"/>: the index i of the lower key, the [0,1]
	// fraction into the segment toward i+1, and whether to hold (Step, or clamped to an endpoint). Binary search.
	private static (int Index, float Frac, bool Step) Locate(ReadOnlySpan<float> times, float time, AnimationInterpolation interpolation)
	{
		var last = times.Length - 1;
		if (last <= 0 || time <= times[0])
			return (0, 0f, true);
		if (time >= times[last])
			return (last, 0f, true);

		// times[lo] <= time < times[lo+1]
		int lo = 0, hi = last;
		while (hi - lo > 1)
		{
			var mid = (lo + hi) >> 1;
			if (times[mid] <= time)
				lo = mid;
			else
				hi = mid;
		}

		if (interpolation == AnimationInterpolation.Step)
			return (lo, 0f, true);
		var span = times[lo + 1] - times[lo];
		var frac = span > 1e-8f ? (time - times[lo]) / span : 0f;
		return (lo, frac, false);
	}
}
