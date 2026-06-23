using System.Numerics;

using Loom.Assets;
using Loom.Core.Animation;

namespace Loom.Tests;

/// <summary>
/// The CPU animation runtime (<see cref="AnimationSampler"/>) — keyframe interpolation, bind-pose
/// fallback for un-keyed channels, hierarchy composition, and the inverse-bind palette. Pure + deterministic.
/// </summary>
public class AnimationSamplerTests
{
	// A 2-joint skeleton: root at origin (identity bind), child translated +1 in Y. Inverse binds are the
	// inverses of the world bind poses (root world = I, child world = translate(0,1,0)).
	private static Skeleton TwoJointSkeleton() => new()
	{
		ParentIndices = [-1, 0],
		LocalBindTransforms = [Matrix4x4.Identity, Matrix4x4.CreateTranslation(0, 1, 0)],
		InverseBindMatrices = [Matrix4x4.Identity, Matrix4x4.CreateTranslation(0, -1, 0)],
		JointNames = ["root", "child"],
	};

	[Test]
	public void Bind_pose_sample_yields_identity_palette()
	{
		var skel = TwoJointSkeleton();
		// A clip whose only channel re-states the child's bind translation at t=0 — sampling reproduces the bind
		// pose, so palette = inverseBind · worldBind = identity for every joint.
		var clip = new AnimationClip
		{
			Name = "rest",
			Duration = 1f,
			Channels =
			[
				new JointChannel
				{
					JointIndex = 1,
					Translation = new Vector3Track
					{
						Times = [0f], Values = [new Vector3(0, 1, 0)], Interpolation = AnimationInterpolation.Linear,
					},
				},
			],
		};

		var palette = new Matrix4x4[2];
		AnimationSampler.Evaluate(skel, clip, 0f, palette);

		MatrixIsApproximately(palette[0], Matrix4x4.Identity);
		MatrixIsApproximately(palette[1], Matrix4x4.Identity);
	}

	[Test]
	public void Unkeyed_joint_keeps_its_bind_pose()
	{
		var skel = TwoJointSkeleton();
		// Clip animates only the root; the child has no channel, so it keeps its bind pose → identity palette entry.
		var clip = new AnimationClip
		{
			Name = "root-only",
			Duration = 1f,
			Channels =
			[
				new JointChannel
				{
					JointIndex = 0,
					Translation = new Vector3Track { Times = [0f], Values = [Vector3.Zero], Interpolation = AnimationInterpolation.Linear },
				},
			],
		};

		var palette = new Matrix4x4[2];
		AnimationSampler.Evaluate(skel, clip, 0.5f, palette);

		MatrixIsApproximately(palette[1], Matrix4x4.Identity); // child unchanged from bind
	}

	[Test]
	public void Translation_track_lerps_between_keyframes()
	{
		var skel = TwoJointSkeleton();
		// Move the ROOT from y=0 to y=2 over 1s; sample at the midpoint → +1. The child rides along (parented),
		// so its palette translates the child's bind-space origin by the root's +1 plus its own (unchanged) offset.
		var clip = new AnimationClip
		{
			Name = "rise",
			Duration = 1f,
			Channels =
			[
				new JointChannel
				{
					JointIndex = 0,
					Translation = new Vector3Track
					{
						Times = [0f, 1f], Values = [Vector3.Zero, new Vector3(0, 2, 0)], Interpolation = AnimationInterpolation.Linear,
					},
				},
			],
		};

		var palette = new Matrix4x4[2];
		AnimationSampler.Evaluate(skel, clip, 0.5f, palette);

		// Root palette = invBind(I) · world(translate +1) = translate(0,1,0).
		palette[0].Translation.Y.ShouldBe(1f, 1e-4f);
		// Child: world = childLocal(+1) · rootWorld(+1) = +2; palette = invBind(-1) · world(+2) → net +1.
		palette[1].Translation.Y.ShouldBe(1f, 1e-4f);
	}

	[Test]
	public void Step_interpolation_holds_the_lower_keyframe()
	{
		var skel = TwoJointSkeleton();
		var clip = new AnimationClip
		{
			Name = "step",
			Duration = 2f,
			Channels =
			[
				new JointChannel
				{
					JointIndex = 0,
					Translation = new Vector3Track
					{
						Times = [0f, 1f, 2f],
						Values = [Vector3.Zero, new Vector3(0, 5, 0), new Vector3(0, 9, 0)],
						Interpolation = AnimationInterpolation.Step,
					},
				},
			],
		};

		var palette = new Matrix4x4[2];
		AnimationSampler.Evaluate(skel, clip, 1.9f, palette); // between key 1 (5) and key 2 (9) → holds 5

		palette[0].Translation.Y.ShouldBe(5f, 1e-4f);
	}

	[Test]
	public void Sampling_past_the_end_clamps_to_the_last_keyframe()
	{
		var skel = TwoJointSkeleton();
		var clip = new AnimationClip
		{
			Name = "clamp",
			Duration = 1f,
			Channels =
			[
				new JointChannel
				{
					JointIndex = 0,
					Translation = new Vector3Track
					{
						Times = [0f, 1f], Values = [Vector3.Zero, new Vector3(0, 4, 0)], Interpolation = AnimationInterpolation.Linear,
					},
				},
			],
		};

		var palette = new Matrix4x4[2];
		AnimationSampler.Evaluate(skel, clip, 99f, palette); // far past the end → last value (4)

		palette[0].Translation.Y.ShouldBe(4f, 1e-4f);
	}

	private static void MatrixIsApproximately(Matrix4x4 actual, Matrix4x4 expected)
	{
		var a = (ReadOnlySpan<float>)[actual.M11, actual.M12, actual.M13, actual.M14, actual.M21, actual.M22, actual.M23, actual.M24, actual.M31, actual.M32, actual.M33, actual.M34, actual.M41, actual.M42, actual.M43, actual.M44];
		var e = (ReadOnlySpan<float>)[expected.M11, expected.M12, expected.M13, expected.M14, expected.M21, expected.M22, expected.M23, expected.M24, expected.M31, expected.M32, expected.M33, expected.M34, expected.M41, expected.M42, expected.M43, expected.M44];
		for (var i = 0; i < 16; i++)
			a[i].ShouldBe(e[i], 1e-4f);
	}
}
