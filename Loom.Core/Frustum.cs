using System.Numerics;

namespace Loom.Core;

/// <summary>
/// A view frustum as six inward-facing <see cref="Plane"/>s (left, right, bottom, top, near, far) in world space —
/// the debug camera's "what would be visible" volume (M13) and the seed for M12's optional CPU frustum culling.
/// Built from a view-projection matrix via <see cref="Matrix4x4FrustumExtensions.ToFrustum"/>; composed entirely of
/// <see cref="System.Numerics"/> types (no bespoke math).
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

	/// <summary>True if <paramref name="point"/> is inside (or on) every plane — the simplest frustum test (M12 seed).</summary>
	public bool Contains(Vector3 point)
	{
		foreach (var plane in Planes)
			if (Plane.DotCoordinate(plane, point) < 0f)
				return false;
		return true;
	}

	/// <summary>True if the world-space sphere is not entirely outside any single plane — the standard cheap
	/// sphere-vs-frustum cull test (M12 seed). Conservative: may keep a sphere that straddles two planes' corners.</summary>
	public bool Intersects(Vector3 center, float radius)
	{
		foreach (var plane in Planes)
			if (Plane.DotCoordinate(plane, center) < -radius)
				return false;
		return true;
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

/// <summary>
/// C# 14 extension members on <see cref="Matrix4x4"/> for frustum math (M13) — kept on the BCL type per the
/// project convention (use <see cref="System.Numerics"/>; add missing operations as extension members, not a
/// bespoke helper class). The receiver is a row-vector view-projection (<c>clip = world · viewProjection</c>).
/// </summary>
public static class Matrix4x4FrustumExtensions
{
	extension(Matrix4x4 viewProjection)
	{
		/// <summary>The 8 world-space corners of the frustum: near face (0..3, CCW from bottom-left) then far face
		/// (4..7), found by unprojecting the NDC cube (x,y ∈ [-1,1], z ∈ [0,1]) through the inverse VP.</summary>
		public Vector3[] GetFrustumCorners()
		{
			Matrix4x4.Invert(viewProjection, out var inverse);
			ReadOnlySpan<Vector3> ndc =
			[
				new(-1f, -1f, 0f), new(1f, -1f, 0f), new(1f, 1f, 0f), new(-1f, 1f, 0f), // near
				new(-1f, -1f, 1f), new(1f, -1f, 1f), new(1f, 1f, 1f), new(-1f, 1f, 1f), // far
			];
			var corners = new Vector3[8];
			for (var i = 0; i < 8; i++)
			{
				var p = Vector4.Transform(new Vector4(ndc[i], 1f), inverse);
				corners[i] = new Vector3(p.X, p.Y, p.Z) / p.W;
			}
			return corners;
		}

		/// <summary>Extract the six world-space frustum planes (Gribb–Hartmann) from the view-projection.</summary>
		public Frustum ToFrustum()
		{
			var m = viewProjection;
			// Rows of the row-major VP; each plane is a combination of the 4th row and one of the others.
			static Plane N(float a, float b, float c, float d) => Plane.Normalize(new Plane(a, b, c, d));
			return new Frustum(
				Left: N(m.M14 + m.M11, m.M24 + m.M21, m.M34 + m.M31, m.M44 + m.M41),
				Right: N(m.M14 - m.M11, m.M24 - m.M21, m.M34 - m.M31, m.M44 - m.M41),
				Bottom: N(m.M14 + m.M12, m.M24 + m.M22, m.M34 + m.M32, m.M44 + m.M42),
				Top: N(m.M14 - m.M12, m.M24 - m.M22, m.M34 - m.M32, m.M44 - m.M42),
				Near: N(m.M13, m.M23, m.M33, m.M43),                       // z ∈ [0,1] near plane
				Far: N(m.M14 - m.M13, m.M24 - m.M23, m.M34 - m.M33, m.M44 - m.M43));
		}
	}
}
