namespace BinaryVibrance.Kaldra.Core.Animation;

/// <summary>
/// How an animation track interpolates between keyframes — mirrors glTF's sampler interpolation modes.
/// </summary>
public enum AnimationInterpolation
{
	/// <summary>Hold the previous keyframe's value until the next (constant segments).</summary>
	Step,

	/// <summary>Linearly interpolate (lerp for vectors, slerp for rotations) between the surrounding keyframes.</summary>
	Linear,

	/// <summary>Cubic-spline interpolation (glTF tangent in/out triplets). Not yet sampled — the loader rejects it explicitly rather than silently degrading.</summary>
	CubicSpline,
}
