using System.Numerics;

namespace Loom.Core.Geometry;

/// <summary>
/// An axis-aligned bounding box (<c>Min</c>/<c>Max</c> corners). The unit of coarse visibility: a mesh carries
/// a local-space box, an instance transforms it to world space, and the CPU frustum cull tests it against the
/// camera's <see cref="Frustum"/>. Backend-neutral pure math (<c>System.Numerics</c>), like <see cref="Camera"/>
/// — culling is an app-owned optimization the engine <em>offers</em>, never a scene graph it mandates
/// ("you hand me draw lists").
/// </summary>
/// <param name="Min">The minimum corner.</param>
/// <param name="Max">The maximum corner.</param>
public readonly record struct Bounds(Vector3 Min, Vector3 Max)
{
	/// <summary>The box centre.</summary>
	public Vector3 Center => (Min + Max) * 0.5f;

	/// <summary>The half-extents from the centre to a corner.</summary>
	public Vector3 Extents => (Max - Min) * 0.5f;

	/// <summary>Build a box from a centre and its half-extents.</summary>
	public static Bounds FromCenterExtents(Vector3 center, Vector3 extents) => new(center - extents, center + extents);

	/// <summary>
	/// Transform this (local-space) box by <paramref name="m"/> and return a world-space box that encloses the
	/// result — the centre is transformed and the extents are scaled by the absolute value of the matrix's linear
	/// part (the standard conservative AABB transform; row-vector convention, matching <see cref="Camera"/>).
	/// </summary>
	public Bounds Transform(Matrix4x4 m)
	{
		var center = Center;
		var extents = Extents;
		var worldCenter = Vector3.Transform(center, m);
		var worldExtents = new Vector3(
			MathF.Abs(m.M11) * extents.X + MathF.Abs(m.M21) * extents.Y + MathF.Abs(m.M31) * extents.Z,
			MathF.Abs(m.M12) * extents.X + MathF.Abs(m.M22) * extents.Y + MathF.Abs(m.M32) * extents.Z,
			MathF.Abs(m.M13) * extents.X + MathF.Abs(m.M23) * extents.Y + MathF.Abs(m.M33) * extents.Z);
		return FromCenterExtents(worldCenter, worldExtents);
	}
}
