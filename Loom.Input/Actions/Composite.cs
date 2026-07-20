namespace Loom.Input;

/// <summary>
/// Composite binding sources that synthesise an analog value from digital buttons: <see cref="Wasd"/> and
/// <see cref="Arrows"/> for keyboard movement, <see cref="Dpad"/> for the gamepad D-pad, and <see cref="Axis"/>
/// for a 1-D pair. The vector convention is right = +X, up = +Y.
/// </summary>
public static class Composite
{
	/// <summary>W/A/S/D as a 2-D movement vector.</summary>
	public static InputBinding Wasd => new()
	{
		Kind = BindingKind.Composite2D,
		Family = InputFamily.Keyboard,
		Code = (int)Key.W,
		Code2 = (int)Key.S,
		Code3 = (int)Key.A,
		Code4 = (int)Key.D,
	};

	/// <summary>The arrow keys as a 2-D movement vector.</summary>
	public static InputBinding Arrows => new()
	{
		Kind = BindingKind.Composite2D,
		Family = InputFamily.Keyboard,
		Code = (int)Key.Up,
		Code2 = (int)Key.Down,
		Code3 = (int)Key.Left,
		Code4 = (int)Key.Right,
	};

	/// <summary>The gamepad D-pad as a 2-D movement vector.</summary>
	public static InputBinding Dpad => new()
	{
		Kind = BindingKind.Composite2D,
		Family = InputFamily.Gamepad,
		Code = (int)GamepadButton.DPadUp,
		Code2 = (int)GamepadButton.DPadDown,
		Code3 = (int)GamepadButton.DPadLeft,
		Code4 = (int)GamepadButton.DPadRight,
	};

	/// <summary>Two keys forming a 1-D axis: <paramref name="negative"/> reads -1, <paramref name="positive"/> reads +1.</summary>
	public static InputBinding Axis(Key negative, Key positive) => new()
	{
		Kind = BindingKind.Composite1D,
		Family = InputFamily.Keyboard,
		Code = (int)negative,
		Code2 = (int)positive,
	};
}
