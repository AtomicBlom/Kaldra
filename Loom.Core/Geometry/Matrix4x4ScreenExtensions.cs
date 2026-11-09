using System.Numerics;

namespace Loom.Core.Geometry;

/// <summary>
/// C# 14 extension members on <see cref="Matrix4x4"/> mapping between world space and a viewport's pixels, beside the
/// frustum helpers. The receiver is the row-vector view-projection an app already hands its shaders
/// (<c>clip = world · viewProjection</c>), with the engine's <c>ClipSpaceCorrection</c> composed into it.
/// <para>
/// That correction decides which way NDC Y runs on screen: it negates Y on a backend whose clip space puts NDC −1 at the
/// top of the target (Vulkan, and DirectX 12 as Loom drives it) and is identity on one that puts +1 there (WebGPU). So
/// every member takes the same correction, read once from <c>IEngineClipSpace.ClipSpaceCorrection</c> when the caller
/// is created, and the same code puts a point on the same pixel on every backend. Viewport pixels run from the
/// top-left corner, +X right and +Y down, and a pixel's centre is at +0.5.
/// </para>
/// </summary>
public static class Matrix4x4ScreenExtensions
{
	extension(Matrix4x4 viewProjection)
	{
		/// <summary>
		/// Project <paramref name="world"/> to viewport pixels. Returns false when the point is on or behind the eye's
		/// plane (clip-space W ≤ 0), where a perspective divide mirrors it through the eye and the pixel means nothing;
		/// <paramref name="screen"/> and <paramref name="depth"/> are then zero. A point in front of the eye projects even
		/// when it falls outside the viewport or past the far plane, so a caller can tell how far off-screen it is.
		/// </summary>
		/// <param name="world">The world-space point.</param>
		/// <param name="viewportSize">The viewport (target) size in pixels.</param>
		/// <param name="clipSpaceCorrection">The engine's clip-space correction, as composed into the receiver.</param>
		/// <param name="screen">The point's position in viewport pixels.</param>
		/// <param name="depth">The point's NDC depth: 0 at the near plane and 1 at the far one, or the reverse under a
		/// reversed-Z projection.</param>
		public bool TryProjectToScreen(Vector3 world, Vector2 viewportSize, in Matrix4x4 clipSpaceCorrection,
			out Vector2 screen, out float depth)
		{
			var clip = Vector4.Transform(new Vector4(world, 1f), viewProjection);
			if (clip.W <= 0f)
			{
				screen = default;
				depth = 0f;
				return false;
			}
			var ndc = new Vector2(clip.X, clip.Y) / clip.W;
			screen = NdcToScreen(ndc, viewportSize, clipSpaceCorrection);
			depth = clip.Z / clip.W;
			return true;
		}

		/// <summary>
		/// The world-space point under viewport pixel <paramref name="screen"/> at NDC depth <paramref name="depth"/>
		/// (0 and 1 give the near and far ends of the ray through that pixel). Returns false when the receiver cannot be
		/// inverted.
		/// </summary>
		public bool TryUnprojectFromScreen(Vector2 screen, float depth, Vector2 viewportSize, in Matrix4x4 clipSpaceCorrection,
			out Vector3 world)
		{
			if (!Matrix4x4.Invert(viewProjection, out var inverse))
			{
				world = default;
				return false;
			}
			var ndc = ScreenToNdc(screen, viewportSize, clipSpaceCorrection);
			var p = Vector4.Transform(new Vector4(ndc, depth, 1f), inverse);
			world = new Vector3(p.X, p.Y, p.Z) / p.W;
			return true;
		}
	}

	// NDC X runs −1 → +1 left to right on every backend; NDC Y runs top to bottom from −1 when the correction negates Y
	// and from +1 when it does not.
	private static Vector2 NdcToScreen(Vector2 ndc, Vector2 viewportSize, in Matrix4x4 clipSpaceCorrection) =>
		new((ndc.X + 1f) * 0.5f * viewportSize.X, (YDown(clipSpaceCorrection) * ndc.Y + 1f) * 0.5f * viewportSize.Y);

	private static Vector2 ScreenToNdc(Vector2 screen, Vector2 viewportSize, in Matrix4x4 clipSpaceCorrection) =>
		new(screen.X / viewportSize.X * 2f - 1f, YDown(clipSpaceCorrection) * (screen.Y / viewportSize.Y * 2f - 1f));

	// +1 when NDC Y increases down the screen (the correction negates Y), −1 when it increases up it.
	private static float YDown(in Matrix4x4 clipSpaceCorrection) => clipSpaceCorrection.M22 < 0f ? 1f : -1f;
}
