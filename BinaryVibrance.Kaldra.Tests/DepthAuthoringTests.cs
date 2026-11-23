using System.Numerics;
using BinaryVibrance.Kaldra.Concepts;
using BinaryVibrance.Kaldra.Core.Graph;
using BinaryVibrance.Kaldra.Core.Materials;
using BinaryVibrance.Kaldra.Graph;

namespace BinaryVibrance.Kaldra.Tests;

/// <summary>
/// The depth knobs a technique and a depth target declare: the clear value, the comparison, and the reversed-Z
/// projection that pairs with them. These are derivation and matrix tests — that the three pieces actually agree on
/// a GPU is the reversed-Z property test in <see cref="RenderPropertyTests"/>.
/// </summary>
public class DepthAuthoringTests
{
	private static AttachmentAction DepthOf(string name, Action<GraphBuilder> declare)
	{
		var plan = BarrierPlan.Derive(GraphSchedule.Derive(RenderGraph.Define(declare)));
		return plan.Passes.SelectMany(p => p.Attachments).First(a => a.Resource.Name == name);
	}

	[Test]
	public void A_depth_target_clears_to_the_far_plane_by_default()
	{
		var depth = DepthOf("depth", g =>
		{
			var d = g.Screen.Depth("depth", Format.Depth32F);
			g.Pass("scene").Writes(g.Backbuffer).WritesDepth(d);
			g.Present(g.Backbuffer);
		});

		depth.Load.ShouldBe(LoadAction.Clear);
		depth.DepthClear.ShouldBe(1f);
	}

	[Test]
	public void A_reversed_z_depth_target_clears_to_zero()
	{
		var depth = DepthOf("depth", g =>
		{
			var d = g.Screen.Depth("depth", Format.Depth32F, clearDepth: 0f);
			g.Pass("scene").Writes(g.Backbuffer).WritesDepth(d);
			g.Present(g.Backbuffer);
		});

		depth.Load.ShouldBe(LoadAction.Clear);
		depth.DepthClear.ShouldBe(0f);
	}

	[Test]
	public void A_no_clear_depth_target_skips_the_clear()
	{
		// noClear was accepted by Domain.Depth's signature and dropped on the floor before this; assert it lands.
		var depth = DepthOf("depth", g =>
		{
			var d = g.Screen.Depth("depth", Format.Depth32F, noClear: true);
			g.Pass("scene").Writes(g.Backbuffer).WritesDepth(d);
			g.Present(g.Backbuffer);
		});

		depth.Load.ShouldBe(LoadAction.DontCare);
		depth.DepthClear.ShouldBeNull();
	}

	[Test]
	public void A_second_pass_writing_the_same_depth_loads_rather_than_clearing()
	{
		var plan = BarrierPlan.Derive(GraphSchedule.Derive(RenderGraph.Define(g =>
		{
			var d = g.Screen.Depth("depth", Format.Depth32F, clearDepth: 0f);
			g.Pass("prepass").Writes(g.Backbuffer).WritesDepth(d);
			g.Pass("scene").Blends(g.Backbuffer).WritesDepth(d);
			g.Present(g.Backbuffer);
		})));

		var second = plan.Passes.Single(p => p.Pass.Name == "scene").Attachments.Single(a => a.Resource.Name == "depth");
		second.Load.ShouldBe(LoadAction.Load);
		second.DepthClear.ShouldBeNull(); // only the clearing pass carries a clear value
	}

	[Test]
	[Arguments(-0.001f)]
	[Arguments(1.001f)]
	[Arguments(255f)]
	public void A_depth_clear_outside_the_depth_range_is_rejected(float clearDepth) =>
		Should.Throw<ArgumentOutOfRangeException>(() =>
			RenderGraph.Define(g => g.Screen.Depth("depth", Format.Depth32F, clearDepth: clearDepth)));

	[Test]
	public void A_technique_tests_less_or_equal_with_no_bias_by_default()
	{
		var descriptor = new TechniqueDescriptor("mesh", HasVertexInput: true);

		descriptor.DepthTest.ShouldBe(DepthCompare.LessOrEqual);
		descriptor.Bias.ShouldBeNull();
	}

	[Test]
	public void The_reversed_projection_puts_the_near_plane_at_one_and_the_far_plane_at_zero()
	{
		// The whole point of reversed-Z, and the piece most easily got subtly wrong: the depth row must be flipped
		// without disturbing x, y, or w. Compose a z-reverse rather than swapping near/far — CreatePerspective-
		// FieldOfView rejects far <= near, so the "pass them backwards" trick throws.
		const float near = 0.1f, far = 100f;
		var standard = new Camera().Perspective(1f, 16f / 9f, near, far).Projection;
		var reversed = new Camera().Perspective(1f, 16f / 9f, near, far, reversed: true).Projection;

		// System.Numerics' perspective is right-handed looking down -Z, mapping depth into [0,1].
		NdcDepth(standard, near).ShouldBe(0f, 1e-4f);
		NdcDepth(standard, far).ShouldBe(1f, 1e-4f);
		NdcDepth(reversed, near).ShouldBe(1f, 1e-4f);
		NdcDepth(reversed, far).ShouldBe(0f, 1e-4f);

		// x and y are untouched, so the reversal changes depth only — not where anything lands on screen.
		var view = new Vector3(0.3f, -0.2f, -5f);
		var s = Project(standard, view);
		var r = Project(reversed, view);
		r.X.ShouldBe(s.X, 1e-5f);
		r.Y.ShouldBe(s.Y, 1e-5f);
	}

	[Test]
	public void The_reversed_projection_keeps_depth_monotonic_towards_the_viewer()
	{
		// Nearer must always mean a *larger* depth under reversed-Z, which is what makes GreaterOrEqual the correct
		// pairing. A sign slip in the composed matrix would still put the planes at 1 and 0 but invert the middle.
		var reversed = new Camera().Perspective(1f, 1f, 0.1f, 100f, reversed: true).Projection;

		var depths = new[] { 0.1f, 1f, 5f, 25f, 100f }.Select(d => NdcDepth(reversed, d)).ToArray();
		for (var i = 1; i < depths.Length; i++)
			depths[i].ShouldBeLessThan(depths[i - 1]);
	}

	// Project a point `distance` in front of a right-handed camera at the origin and return its post-divide depth.
	private static float NdcDepth(Matrix4x4 projection, float distance) => Project(projection, new Vector3(0, 0, -distance)).Z;

	private static Vector3 Project(Matrix4x4 projection, Vector3 viewSpace)
	{
		var clip = Vector4.Transform(new Vector4(viewSpace, 1f), projection);
		return new Vector3(clip.X, clip.Y, clip.Z) / clip.W;
	}
}
