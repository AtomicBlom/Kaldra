namespace Loom.Input;

/// <summary>
/// Mouse binding sources for action definitions. Buttons convert straight to a button binding;
/// <see cref="Wheel"/>/<see cref="WheelX"/> produce axis sources reading the frame's scroll delta.
/// </summary>
public static class Mouse
{
	public static InputBinding Left => MouseButton.Left;
	public static InputBinding Right => MouseButton.Right;
	public static InputBinding Middle => MouseButton.Middle;
	public static InputBinding Button4 => MouseButton.Button4;
	public static InputBinding Button5 => MouseButton.Button5;

	/// <summary>The vertical scroll-wheel delta as an axis source.</summary>
	public static InputBinding Wheel => new() { Kind = BindingKind.MouseWheelY, Family = InputFamily.Mouse };

	/// <summary>The horizontal scroll-wheel delta as an axis source.</summary>
	public static InputBinding WheelX => new() { Kind = BindingKind.MouseWheelX, Family = InputFamily.Mouse };
}
