using System.Numerics;

using BinaryVibrance.Kaldra.Core.Hosting;
using BinaryVibrance.Kaldra.Input;

namespace BinaryVibrance.Kaldra.Concepts;

/// <summary>
/// A free-fly debug camera: WASD to move on the view plane, Q/E down/up, right-mouse-drag to look. It drives a
/// <see cref="Camera"/> in free-look mode (position + yaw/pitch) from a neutral <see cref="InputSnapshot"/> — the
/// same resolved input any consumer polls — so it names no windowing library. Takes the engine's
/// <see cref="IEngineClipSpace"/> so the camera it produces is clip-corrected like any other <see cref="Camera"/>.
/// </summary>
public sealed class FlyCamera(IEngineClipSpace clip)
{
	public Vector3 Position;
	public float Yaw;   // radians; 0 looks toward -Z
	public float Pitch; // radians; clamped to avoid gimbal flip
	public float MoveSpeed = 4f;
	public float LookSensitivity = 0.0035f;

	private readonly Camera _camera = new(clip);

	/// <summary>Place the camera at <paramref name="eye"/> looking at <paramref name="target"/> (used when the debug
	/// camera is first activated, so it starts where the scripted camera was).</summary>
	public void PlaceLookingAt(Vector3 eye, Vector3 target)
	{
		Position = eye;
		var dir = Vector3.Normalize(target - eye);
		Pitch = MathF.Asin(Math.Clamp(dir.Y, -1f, 1f));
		Yaw = MathF.Atan2(dir.X, -dir.Z);
	}

	private Vector3 Forward => new(
		MathF.Sin(Yaw) * MathF.Cos(Pitch),
		MathF.Sin(Pitch),
		-MathF.Cos(Yaw) * MathF.Cos(Pitch));

	/// <summary>Advance the camera from this frame's <paramref name="input"/> and return it (free-look view +
	/// perspective projection). Look while the right mouse button is held (drag the pointer), move with WASD and Q/E.</summary>
	public Camera Update(InputSnapshot input, float deltaSeconds, float aspect)
	{
		if (input.Down(MouseButton.Right))
		{
			var delta = input.PointerDelta;
			Yaw += delta.X * LookSensitivity;
			Pitch = Math.Clamp(Pitch - delta.Y * LookSensitivity, -1.5f, 1.5f);
		}

		var forward = Forward;
		var right = Vector3.Normalize(Vector3.Cross(forward, Vector3.UnitY));
		var step = MoveSpeed * deltaSeconds;
		if (input.Down(Key.W)) Position += forward * step;
		if (input.Down(Key.S)) Position -= forward * step;
		if (input.Down(Key.D)) Position += right * step;
		if (input.Down(Key.A)) Position -= right * step;
		if (input.Down(Key.E)) Position += Vector3.UnitY * step;
		if (input.Down(Key.Q)) Position -= Vector3.UnitY * step;

		return _camera.FreeLook(Position, Yaw, Pitch).Perspective(MathF.PI / 3f, aspect, 0.1f, 100f);
	}
}
