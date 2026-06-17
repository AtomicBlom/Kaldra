using System.Numerics;

namespace Loom.Core;

/// <summary>
/// A view frustum as six world-space planes, extracted from a camera's view-projection
/// (<see cref="Camera.ExtractFrustum"/>). Each plane is oriented so the inside half-space is positive
/// (<see cref="Plane.DotCoordinate"/> ≥ 0). The CPU frustum cull tests an instance's world <see cref="Bounds"/>
/// against all six with the conservative "positive vertex" test — fast, and only ever rejects boxes that are
/// fully outside, so it never culls something visible.
/// <para>
/// Extracted from the <em>neutral</em> view-projection (right-handed, +Y up, z ∈ [0,1] — the host's
/// <c>CreatePerspectiveFieldOfView</c> convention), so culling is backend-independent: the per-backend
/// clip-space Y-flip only affects rasterization NDC, not which world objects are in view.
/// </para>
/// </summary>
public readonly struct Frustum
{
	private readonly Plane _left, _right, _bottom, _top, _near, _far;

	internal Frustum(Plane left, Plane right, Plane bottom, Plane top, Plane near, Plane far)
	{
		_left = left;
		_right = right;
		_bottom = bottom;
		_top = top;
		_near = near;
		_far = far;
	}

	/// <summary>True if <paramref name="bounds"/> is at least partially inside the frustum (conservatively — may keep a box that is just outside a corner, but never culls one that is visible).</summary>
	public bool Intersects(in Bounds bounds) =>
		!(IsOutside(_left, bounds) || IsOutside(_right, bounds) || IsOutside(_bottom, bounds)
			|| IsOutside(_top, bounds) || IsOutside(_near, bounds) || IsOutside(_far, bounds));

	// The box is fully outside a plane when its "positive vertex" (the corner farthest along the plane normal)
	// is still behind the plane — then every corner is behind it, so the whole box is culled by this plane.
	private static bool IsOutside(Plane plane, in Bounds bounds)
	{
		var n = plane.Normal;
		var positive = new Vector3(
			n.X >= 0 ? bounds.Max.X : bounds.Min.X,
			n.Y >= 0 ? bounds.Max.Y : bounds.Min.Y,
			n.Z >= 0 ? bounds.Max.Z : bounds.Min.Z);
		return Plane.DotCoordinate(plane, positive) < 0f;
	}
}
