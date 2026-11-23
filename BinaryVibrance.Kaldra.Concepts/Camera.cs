using System.Numerics;

using BinaryVibrance.Kaldra.Core.Geometry;
using BinaryVibrance.Kaldra.Core.Hosting;

namespace BinaryVibrance.Kaldra.Concepts;

/// <summary>
/// A 3D camera the app drives and reads a view-projection from. It owns the one piece of backend awareness an app
/// otherwise has to get right by hand: it takes the engine's <see cref="IEngineClipSpace"/> and composes its
/// <see cref="IEngineClipSpace.ClipSpaceCorrection"/> into <see cref="ViewProjection"/>, so the matrix it hands a
/// shader lands in clip space on Vulkan and DirectX12 alike — the app never names a backend's NDC convention. Pass
/// <c>null</c> for the clip space to skip the correction (a unit test, or a target that needs none).
/// <para>
/// The view is set by one of the mode helpers — <see cref="LookAt"/> (fixed target), <see cref="FreeLook"/>
/// (FPS-style position + yaw/pitch), or <see cref="Chase"/> (third-person follow) — and the projection by
/// <see cref="Perspective"/> or <see cref="Orthographic"/>; each returns the camera so calls chain. Publish the
/// result to a <c>g.Constants&lt;Matrix4x4&gt;</c> buffer a geometry pass <c>.Reads</c> (e.g.
/// <c>engine.Write(handle, camera.ViewProjection)</c>) each frame for a moving view.
/// </para>
/// </summary>
public sealed class Camera(IEngineClipSpace? clipSpace = null)
{
	/// <summary>World → view (camera) space. Set by a mode helper, or assigned directly to mirror an external view.</summary>
	public Matrix4x4 View { get; set; } = Matrix4x4.Identity;

	/// <summary>View → clip space (neutral: right-handed, +Y up, z ∈ [0,1]). Set by <see cref="Perspective"/>/<see cref="Orthographic"/>.</summary>
	public Matrix4x4 Projection { get; set; } = Matrix4x4.Identity;

	/// <summary>
	/// The clip-corrected view-projection to hand a shader (row-vector order <c>view * projection</c>, then the
	/// engine's <see cref="IEngineClipSpace.ClipSpaceCorrection"/>). With no clip space, the correction is identity.
	/// </summary>
	public Matrix4x4 ViewProjection => View * Projection * (clipSpace?.ClipSpaceCorrection ?? Matrix4x4.Identity);

	/// <summary>
	/// The six world-space frustum planes from the <em>uncorrected</em> view-projection (the clip-space correction
	/// is a backend NDC fix; culling is world-space and convention-neutral), for optional CPU frustum culling.
	/// </summary>
	public Frustum ExtractFrustum() => (View * Projection).ToFrustum();

	/// <summary>
	/// Set a perspective <see cref="Projection"/> from a vertical field of view (radians), aspect, and near/far planes.
	/// <para>
	/// <paramref name="reversed"/> maps the near plane to depth 1 and the far plane to depth 0 — <b>reversed-Z</b>,
	/// which cancels the perspective divide's precision loss at distance against a float depth buffer's density near
	/// zero, and is the standard fix for depth fighting in a large world. It is one of three pieces that must agree:
	/// declare the depth target with <c>Domain.Depth(name, format, clearDepth: 0f)</c> and give every technique
	/// drawing into it <c>DepthTest: DepthCompare.GreaterOrEqual</c>. Getting one wrong inverts or erases occlusion
	/// rather than failing.
	/// </para>
	/// </summary>
	public Camera Perspective(float fovY, float aspect, float near, float far, bool reversed = false)
	{
		var projection = Matrix4x4.CreatePerspectiveFieldOfView(fovY, aspect, near, far);
		// Compose z' = 1 - z rather than swapping near/far: CreatePerspectiveFieldOfView rejects far <= near, so the
		// usual "pass them backwards" trick throws here. Post-multiplying flips the already-computed depth row.
		if (reversed)
		{
			projection *= new Matrix4x4(
				1, 0, 0, 0,
				0, 1, 0, 0,
				0, 0, -1, 0,
				0, 0, 1, 1);
		}
		Projection = projection;
		return this;
	}

	/// <summary>Set an orthographic <see cref="Projection"/> spanning <paramref name="width"/>×<paramref name="height"/> world units between the near/far planes.</summary>
	public Camera Orthographic(float width, float height, float near, float far)
	{
		Projection = Matrix4x4.CreateOrthographic(width, height, near, far);
		return this;
	}

	/// <summary>Look from <paramref name="eye"/> at a fixed <paramref name="target"/> (up defaults to +Y).</summary>
	public Camera LookAt(Vector3 eye, Vector3 target, Vector3? up = null)
	{
		View = Matrix4x4.CreateLookAt(eye, target, up ?? Vector3.UnitY);
		return this;
	}

	/// <summary>
	/// FPS-style free-look: sit at <paramref name="position"/> and face the direction given by <paramref name="yaw"/>
	/// (radians about +Y; 0 looks toward −Z) and <paramref name="pitch"/> (radians, +up). Drive yaw/pitch from mouse
	/// deltas and the position from movement keys for a fly camera.
	/// </summary>
	public Camera FreeLook(Vector3 position, float yaw, float pitch)
	{
		var forward = new Vector3(
			MathF.Cos(pitch) * MathF.Sin(yaw),
			MathF.Sin(pitch),
			-MathF.Cos(pitch) * MathF.Cos(yaw));
		View = Matrix4x4.CreateLookAt(position, position + forward, Vector3.UnitY);
		return this;
	}

	/// <summary>
	/// Third-person chase: sit <paramref name="distance"/> behind <paramref name="target"/> along
	/// <paramref name="back"/> (the direction from target to camera, normalized here) and <paramref name="height"/>
	/// above it, looking at the target. Feed a follow target's position and facing each frame.
	/// </summary>
	public Camera Chase(Vector3 target, Vector3 back, float distance, float height, Vector3? up = null)
	{
		var eye = target + Vector3.Normalize(back) * distance + Vector3.UnitY * height;
		View = Matrix4x4.CreateLookAt(eye, target, up ?? Vector3.UnitY);
		return this;
	}
}
