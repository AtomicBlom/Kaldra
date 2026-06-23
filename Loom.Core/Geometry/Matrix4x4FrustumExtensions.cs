using System.Numerics;

namespace Loom.Core.Geometry;

/// <summary>
/// C# 14 extension members on <see cref="Matrix4x4"/> for frustum math — kept on the BCL type per the
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
