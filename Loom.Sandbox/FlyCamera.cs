using System.Numerics;

using Loom.Core;

using Silk.NET.Input;

namespace Loom.Sandbox;

/// <summary>
/// A free-fly debug camera (M13 detached camera): WASD to move on the view plane, Q/E down/up, right-mouse-drag to
/// look. Host-side only — it just produces a <see cref="Camera"/> the host feeds to the engine, so the engine stays
/// unaware there even is a debug camera. Neutral right-handed, +Y-up projection (the backend applies its clip-space
/// fix-up), matching the demos' orbit camera.
/// </summary>
internal sealed class FlyCamera
{
	public Vector3 Position;
	public float Yaw;   // radians; 0 looks toward -Z
	public float Pitch; // radians; clamped to avoid gimbal flip
	public float MoveSpeed = 4f;
	public float LookSensitivity = 0.0035f;

	private Vector2 _lastMouse;
	private bool _looking;

	/// <summary>Place the camera at <paramref name="eye"/> looking at <paramref name="target"/> (used when the debug
	/// camera is first activated, so it starts where the scripted camera was).</summary>
	public void PlaceLookingAt(Vector3 eye, Vector3 target)
	{
		Position = eye;
		var dir = Vector3.Normalize(target - eye);
		Pitch = MathF.Asin(Math.Clamp(dir.Y, -1f, 1f));
		Yaw = MathF.Atan2(dir.X, -dir.Z);
		_looking = false;
	}

	private Vector3 Forward => new(
		MathF.Sin(Yaw) * MathF.Cos(Pitch),
		MathF.Sin(Pitch),
		-MathF.Cos(Yaw) * MathF.Cos(Pitch));

	/// <summary>Advance the camera from this frame's input and return its view+projection.</summary>
	public Camera Update(IKeyboard? keyboard, IMouse? mouse, float deltaSeconds, float aspect)
	{
		if (mouse is not null)
		{
			var pos = mouse.Position;
			if (mouse.IsButtonPressed(MouseButton.Right))
			{
				if (_looking)
				{
					var d = pos - _lastMouse;
					Yaw += d.X * LookSensitivity;
					Pitch = Math.Clamp(Pitch - d.Y * LookSensitivity, -1.5f, 1.5f);
				}
				_looking = true;
			}
			else
			{
				_looking = false;
			}
			_lastMouse = pos;
		}

		var forward = Forward;
		var right = Vector3.Normalize(Vector3.Cross(forward, Vector3.UnitY));
		var step = MoveSpeed * deltaSeconds;
		if (keyboard is not null)
		{
			if (keyboard.IsKeyPressed(Key.W)) Position += forward * step;
			if (keyboard.IsKeyPressed(Key.S)) Position -= forward * step;
			if (keyboard.IsKeyPressed(Key.D)) Position += right * step;
			if (keyboard.IsKeyPressed(Key.A)) Position -= right * step;
			if (keyboard.IsKeyPressed(Key.E)) Position += Vector3.UnitY * step;
			if (keyboard.IsKeyPressed(Key.Q)) Position -= Vector3.UnitY * step;
		}

		var view = Matrix4x4.CreateLookAt(Position, Position + forward, Vector3.UnitY);
		var projection = Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 3f, aspect, 0.1f, 100f);
		return new Camera(view, projection);
	}
}
