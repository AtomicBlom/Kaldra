using System.Numerics;

using Loom.Core;

namespace Loom.Tests;

/// <summary>M13 <see cref="Frustum"/> + the <see cref="Matrix4x4"/> frustum extension members.</summary>
public class FrustumTests
{
	// An eye at +Z=5 looking at the origin down -Z: near plane 0.5, far 20, square aspect.
	private static Matrix4x4 ViewProjection()
	{
		var view = Matrix4x4.CreateLookAt(new Vector3(0, 0, 5), Vector3.Zero, Vector3.UnitY);
		var proj = Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 3f, 1f, 0.5f, 20f);
		return view * proj;
	}

	[Test]
	public void GetFrustumCorners_returns_eight_with_near_closer_than_far()
	{
		var corners = ViewProjection().GetFrustumCorners();
		corners.Length.ShouldBe(8);
		// Corners 0..3 are the near face, 4..7 the far face. Looking down -Z from +Z, the near face sits at a
		// larger world Z (closer to the eye) than the far face.
		for (var i = 0; i < 4; i++)
			(corners[i].Z > corners[i + 4].Z).ShouldBeTrue($"near corner {i} should be closer to the +Z eye than its far corner");
	}

	[Test]
	public void Contains_accepts_points_in_view_and_rejects_points_behind_the_camera()
	{
		var frustum = ViewProjection().ToFrustum();
		frustum.Contains(Vector3.Zero).ShouldBeTrue();               // the origin is centred in view, between near and far
		frustum.Contains(new Vector3(0, 0, 100)).ShouldBeFalse();    // far behind the eye — outside the near plane
		frustum.Contains(new Vector3(100, 0, 0)).ShouldBeFalse();    // far to the side — outside a lateral plane
	}

	[Test]
	public void Intersects_is_more_permissive_than_Contains_for_a_straddling_sphere()
	{
		var frustum = ViewProjection().ToFrustum();
		var justOutside = new Vector3(0, 0, 100);
		frustum.Contains(justOutside).ShouldBeFalse();
		frustum.Intersects(justOutside, 200f).ShouldBeTrue(); // a huge sphere centred behind still overlaps the frustum
	}
}
