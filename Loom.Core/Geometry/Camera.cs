using System.Numerics;

namespace Loom.Core.Geometry;

/// <summary>
/// The view onto the scene a pass renders from: a view matrix and a projection. Per-frame data the host
/// updates and the pass pulls from <see cref="Camera"/> (never captured — §8). Backend-neutral
/// (<c>System.Numerics</c>); matrices follow the row-vector convention (<c>clip = position · MVP</c>), so the
/// combined transform composes left-to-right. The host builds <see cref="Projection"/> in a neutral convention
/// (right-handed, +Y up); the backend applies its own clip-space fix-up on ingest — the Vulkan +Y-down Y-flip,
/// identity on a +Y-up API like D3D12 — so no API-specific NDC convention is baked into host or Core code
/// (Planning.md §8 portability; M7-B5).
/// </summary>
/// <param name="ViewMatrix">World → view (camera) space.</param>
/// <param name="Projection">View → clip space.</param>
public readonly record struct Camera(Matrix4x4 ViewMatrix, Matrix4x4 Projection)
{
	/// <summary>The combined view-projection (no model). Row-vector order: <c>ViewMatrix * Projection</c>.</summary>
	public Matrix4x4 ViewProjection => ViewMatrix * Projection;

	/// <summary>An identity camera (clip space == world space) — the default before the host sets one.</summary>
	public static Camera Identity => new(Matrix4x4.Identity, Matrix4x4.Identity);

	/// <summary>
	/// Extract the six world-space frustum planes from <see cref="ViewProjection"/> (Gribb–Hartmann), for the
	/// engine's optional CPU frustum cull (M12). Uses the neutral projection convention (right-handed, +Y up,
	/// z ∈ [0,1]), so the near plane is the bare z-column; the result is backend-independent. Each plane is
	/// normalized and oriented inside-positive — see <see cref="Frustum"/>.
	/// </summary>
	public Frustum ExtractFrustum() => ViewProjection.ToFrustum();

	/// <summary>
	/// This camera with its projection's Y axis flipped (negated <c>M22</c>) — the clip-space fix-up a backend
	/// applies on ingest so a host-supplied +Y-up projection renders upright. Shared so every backend that needs
	/// the flip negates identically rather than hand-rolling the same matrix tweak.
	/// </summary>
	public Camera FlipClipY()
	{
		var projection = Projection;
		projection.M22 *= -1f;
		return this with { Projection = projection };
	}
}
