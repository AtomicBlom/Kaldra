using System.Numerics;

using Loom.Concepts;

namespace Loom.Web.Demos;

/// <summary>
/// The shared orbiting viewpoint the 3D demos animate — and, crucially, the exact camera the desktop sandbox uses
/// for a <c>--capture</c> frame (its non-overriding demos all fall back to this same orbit). Matching it here means a
/// browser capture at the frozen time lands on the identical viewpoint as the Vulkan reference, so the two are
/// directly pixel-comparable. Any demo that does not need a bespoke framing should drive its camera through this.
/// </summary>
internal static class DemoCamera
{
	/// <summary>
	/// Point <paramref name="camera"/> at the origin from the shared orbit at time <paramref name="time"/>: a 2.5-unit
	/// radius circle at eye-height 1.6, a 60° vertical field of view. At the capture time (1.0) this is byte-for-byte
	/// the desktop sandbox's scripted capture camera.
	/// </summary>
	public static void Orbit(Camera camera, float time, float aspect)
	{
		var eye = new Vector3(MathF.Sin(time) * 2.5f, 1.6f, MathF.Cos(time) * 2.5f);
		camera.Perspective(MathF.PI / 3f, aspect, 0.1f, 100f).LookAt(eye, Vector3.Zero);
	}
}
