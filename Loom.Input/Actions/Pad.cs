namespace Loom.Input;

/// <summary>
/// Gamepad binding sources for action definitions. Face buttons and the D-pad convert straight to a button
/// binding; <see cref="LeftStick"/>/<see cref="RightStick"/> produce 2-D stick sources and <see cref="Triggers"/>
/// a single axis from the trigger pair. All default to gamepad 0; use <see cref="OnPad"/> to target another pad.
/// </summary>
public static class Pad
{
	/// <summary>Bottom face button.</summary>
	public static InputBinding South => GamepadButton.South;

	/// <summary>Right face button.</summary>
	public static InputBinding East => GamepadButton.East;

	/// <summary>Left face button.</summary>
	public static InputBinding West => GamepadButton.West;

	/// <summary>Top face button.</summary>
	public static InputBinding North => GamepadButton.North;

	public static InputBinding LeftBumper => GamepadButton.LeftBumper;
	public static InputBinding RightBumper => GamepadButton.RightBumper;
	public static InputBinding Back => GamepadButton.Back;
	public static InputBinding Start => GamepadButton.Start;
	public static InputBinding Guide => GamepadButton.Guide;
	public static InputBinding LeftStickButton => GamepadButton.LeftStick;
	public static InputBinding RightStickButton => GamepadButton.RightStick;
	public static InputBinding DPadUp => GamepadButton.DPadUp;
	public static InputBinding DPadDown => GamepadButton.DPadDown;
	public static InputBinding DPadLeft => GamepadButton.DPadLeft;
	public static InputBinding DPadRight => GamepadButton.DPadRight;

	/// <summary>The left stick as a 2-D vector source.</summary>
	public static InputBinding LeftStick => new()
	{
		Kind = BindingKind.Stick,
		Family = InputFamily.Gamepad,
		Code = (int)GamepadAxis.LeftX,
		Code2 = (int)GamepadAxis.LeftY,
	};

	/// <summary>The right stick as a 2-D vector source.</summary>
	public static InputBinding RightStick => new()
	{
		Kind = BindingKind.Stick,
		Family = InputFamily.Gamepad,
		Code = (int)GamepadAxis.RightX,
		Code2 = (int)GamepadAxis.RightY,
	};

	/// <summary>The trigger pair as one axis (right minus left).</summary>
	public static InputBinding Triggers => new() { Kind = BindingKind.Triggers, Family = InputFamily.Gamepad };

	/// <summary>The left trigger as a single axis.</summary>
	public static InputBinding LeftTrigger => GamepadAxis.LeftTrigger;

	/// <summary>The right trigger as a single axis.</summary>
	public static InputBinding RightTrigger => GamepadAxis.RightTrigger;

	/// <summary>Retargets a gamepad binding to pad <paramref name="index"/>.</summary>
	public static InputBinding OnPad(InputBinding binding, int index) => binding with { PadIndex = index };
}
