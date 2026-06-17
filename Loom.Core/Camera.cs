using System.Numerics;

namespace Loom.Core;

/// <summary>
/// The view onto the scene a pass renders from: a view matrix and a projection. Per-frame data the host
/// updates and the pass pulls from <see cref="IRenderContext.Camera"/> (never captured — §8). Backend-neutral
/// (<c>System.Numerics</c>); matrices follow the row-vector convention (<c>clip = position · MVP</c>), so the
/// combined transform composes left-to-right. The host builds <see cref="Projection"/> in a neutral convention
/// (right-handed, +Y up); the backend applies its own clip-space fix-up on ingest — the Vulkan +Y-down Y-flip,
/// identity on a +Y-up API like D3D12 — so no API-specific NDC convention is baked into host or Core code
/// (Planning.md §8 portability; M7-B5).
/// </summary>
/// <param name="View">World → view (camera) space.</param>
/// <param name="Projection">View → clip space.</param>
public readonly record struct Camera(Matrix4x4 View, Matrix4x4 Projection)
{
	/// <summary>The combined view-projection (no model). Row-vector order: <c>View * Projection</c>.</summary>
	public Matrix4x4 ViewProjection => View * Projection;

	/// <summary>An identity camera (clip space == world space) — the default before the host sets one.</summary>
	public static Camera Identity => new(Matrix4x4.Identity, Matrix4x4.Identity);

	/// <summary>
	/// Extract the six world-space frustum planes from <see cref="ViewProjection"/> (Gribb–Hartmann), for the
	/// engine's optional CPU frustum cull (M12). Uses the neutral projection convention (right-handed, +Y up,
	/// z ∈ [0,1]), so the near plane is the bare z-column; the result is backend-independent. Each plane is
	/// normalized and oriented inside-positive — see <see cref="Frustum"/>.
	/// </summary>
	public Frustum ExtractFrustum()
	{
		var m = ViewProjection;
		// Columns of m (row-vector convention: a clip component is the dot of the homogeneous point with a column).
		var col1 = new Vector4(m.M11, m.M21, m.M31, m.M41); // x
		var col2 = new Vector4(m.M12, m.M22, m.M32, m.M42); // y
		var col3 = new Vector4(m.M13, m.M23, m.M33, m.M43); // z
		var col4 = new Vector4(m.M14, m.M24, m.M34, m.M44); // w
		return new Frustum(
			ToPlane(col4 + col1), // left:   x ≥ -w
			ToPlane(col4 - col1), // right:  x ≤  w
			ToPlane(col4 + col2), // bottom: y ≥ -w
			ToPlane(col4 - col2), // top:    y ≤  w
			ToPlane(col3),        // near:   z ≥  0  (z ∈ [0,1] NDC)
			ToPlane(col4 - col3)); // far:    z ≤  w
	}

	private static Plane ToPlane(Vector4 v) => Plane.Normalize(new Plane(v.X, v.Y, v.Z, v.W));
}
