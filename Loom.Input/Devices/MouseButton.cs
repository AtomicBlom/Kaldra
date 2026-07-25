namespace Loom.Input;

/// <summary>
/// A neutral mouse button. Values are contiguous from zero so a button doubles as a bit index into the
/// snapshot's held-button mask.
/// </summary>
public enum MouseButton
{
	Left = 0,
	Right,
	Middle,

	/// <summary>The first extended button (typically "back").</summary>
	Button4,

	/// <summary>The second extended button (typically "forward").</summary>
	Button5,
}
