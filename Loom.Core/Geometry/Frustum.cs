using System.Numerics;

namespace Loom.Core.Geometry;

/// <summary>
/// A view frustum as six inward-facing <see cref="Plane"/>s (left, right, bottom, top, near, far) in world space —
/// the debug camera's "what would be visible" volume and the engine's optional CPU frustum-cull primitive.
/// Each plane is oriented so the inside half-space is positive (<see cref="Plane.DotCoordinate"/> ≥ 0).
/// Built from a view-projection matrix via <see cref="Matrix4x4FrustumExtensions.ToFrustum"/> (or
/// <see cref="Camera.ExtractFrustum"/>); composed entirely of <see cref="System.Numerics"/> types (no bespoke math).
/// <para>
/// Extracted from the <em>neutral</em> view-projection (right-handed, +Y up, z ∈ [0,1] — the host's
/// <c>CreatePerspectiveFieldOfView</c> convention), so culling is backend-independent: the per-backend
/// clip-space Y-flip only affects rasterization NDC, not which world objects are in view.
/// </para>
/// </summary>
/// <param name="Left">Left clip plane (normal points into the frustum).</param>
/// <param name="Right">Right clip plane.</param>
/// <param name="Bottom">Bottom clip plane.</param>
/// <param name="Top">Top clip plane.</param>
/// <param name="Near">Near clip plane.</param>
/// <param name="Far">Far clip plane.</param>
public readonly record struct Frustum(Plane Left, Plane Right, Plane Bottom, Plane Top, Plane Near, Plane Far)
{
	/// <summary>The six planes in order (left, right, bottom, top, near, far) — handy for culling loops.</summary>
	public ReadOnlySpan<Plane> Planes => new[] { Left, Right, Bottom, Top, Near, Far };

	/// <summary>True if <paramref name="point"/> is inside (or on) every plane — the simplest frustum test.</summary>
	public bool Contains(Vector3 point)
	{
		foreach (var plane in Planes)
			if (Plane.DotCoordinate(plane, point) < 0f)
				return false;
		return true;
	}

	/// <summary>True if the world-space sphere is not entirely outside any single plane — the standard cheap
	/// sphere-vs-frustum cull test. Conservative: may keep a sphere that straddles two planes' corners.</summary>
	public bool Intersects(Vector3 center, float radius)
	{
		foreach (var plane in Planes)
			if (Plane.DotCoordinate(plane, center) < -radius)
				return false;
		return true;
	}

	/// <summary>True if <paramref name="bounds"/> is at least partially inside the frustum — the conservative
	/// "positive vertex" AABB test the <see cref="Scene"/> cull uses (may keep a box that is just outside a
	/// corner, but never culls one that is visible).</summary>
	public bool Intersects(in Bounds bounds) =>
		!(IsOutside(Left, bounds) || IsOutside(Right, bounds) || IsOutside(Bottom, bounds)
			|| IsOutside(Top, bounds) || IsOutside(Near, bounds) || IsOutside(Far, bounds));

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

	/// <summary>The 12 corner-index pairs of the frustum's edges, for wireframe drawing of
	/// <see cref="Matrix4x4FrustumExtensions.GetFrustumCorners"/> (near quad, far quad, then the four connectors).</summary>
	public static ReadOnlySpan<int> EdgeIndices =>
	[
		0, 1, 1, 2, 2, 3, 3, 0, // near quad
		4, 5, 5, 6, 6, 7, 7, 4, // far quad
		0, 4, 1, 5, 2, 6, 3, 7, // connecting edges
	];
}
