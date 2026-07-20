namespace Loom.Input;

/// <summary>
/// A neutral gamepad analog axis. Stick axes range roughly [-1, 1] (left/down negative, right/up positive);
/// trigger axes range [0, 1]. Values are contiguous from zero so an axis doubles as an index into the snapshot's
/// per-pad axis buffer.
/// </summary>
public enum GamepadAxis
{
	LeftX = 0,
	LeftY,
	RightX,
	RightY,
	LeftTrigger,
	RightTrigger,
}
