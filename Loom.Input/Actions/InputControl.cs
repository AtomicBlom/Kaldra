namespace Loom.Input;

/// <summary>What sort of physical control an <see cref="InputControl"/> is.</summary>
public enum ControlKind : byte
{
	/// <summary>A keyboard key: <see cref="InputControl.Code"/> is a <see cref="Key"/>.</summary>
	Key,

	/// <summary>A mouse button: <see cref="InputControl.Code"/> is a <see cref="MouseButton"/>.</summary>
	MouseButton,

	/// <summary>The scroll wheel: <see cref="InputControl.Code"/> 0 vertical, 1 horizontal.</summary>
	MouseWheel,

	/// <summary>A gamepad button: <see cref="InputControl.Code"/> is a <see cref="GamepadButton"/>.</summary>
	PadButton,

	/// <summary>One axis of a stick or a trigger: <see cref="InputControl.Code"/> is a <see cref="GamepadAxis"/>, and
	/// <see cref="InputControl.Sign"/> says which way, for one direction of a stick.</summary>
	PadAxis,

	/// <summary>A whole stick: <see cref="InputControl.Code"/> 0 the left, 1 the right.</summary>
	PadStick,

	/// <summary>The two triggers as one axis (right minus left).</summary>
	PadTriggers,
}

/// <summary>
/// One physical control a binding reads, for drawing it (a glyph) or naming it: a key, a mouse button, a pad button, an
/// axis (or one direction of it), a stick. <see cref="InputBindingDescription.Controls"/> breaks a binding into its
/// controls in order (a composite into its keys); <see cref="InputBindingDescription.Name(InputControl, GamepadType)"/>
/// names one as the pad in hand labels it.
/// </summary>
/// <param name="Kind">What sort of control it is.</param>
/// <param name="Code">Which: a <see cref="Key"/>, <see cref="MouseButton"/>, <see cref="GamepadButton"/> or
/// <see cref="GamepadAxis"/> as <see cref="Kind"/> says.</param>
/// <param name="Sign">For one direction of an axis: -1 (left, up) or 1 (right, down); 0 for the whole axis.</param>
public readonly record struct InputControl(ControlKind Kind, int Code, int Sign = 0)
{
	public static InputControl Of(Key key) => new(ControlKind.Key, (int)key);
	public static InputControl Of(MouseButton button) => new(ControlKind.MouseButton, (int)button);
	public static InputControl Of(GamepadButton button) => new(ControlKind.PadButton, (int)button);
	public static InputControl Of(GamepadAxis axis, int sign = 0) => new(ControlKind.PadAxis, (int)axis, sign);

	/// <summary>Whether it is a pad's control (so drawn and named as the pad in hand labels it).</summary>
	public bool OnPad => Kind is ControlKind.PadButton or ControlKind.PadAxis or ControlKind.PadStick or ControlKind.PadTriggers;

	/// <summary>The control as a binding of its own: a button binding for a key or button, an axis binding for an axis.
	/// A composite's half becomes the key alone; one direction of a stick, the whole axis.</summary>
	public InputBinding ToBinding(int padIndex = Pad.Last) => Kind switch
	{
		ControlKind.Key => (Key)Code,
		ControlKind.MouseButton => (MouseButton)Code,
		ControlKind.MouseWheel => new InputBinding { Kind = Code == 0 ? BindingKind.MouseWheelY : BindingKind.MouseWheelX, Family = InputFamily.Mouse },
		ControlKind.PadButton => ((InputBinding)(GamepadButton)Code) with { PadIndex = padIndex },
		ControlKind.PadAxis => ((InputBinding)(GamepadAxis)Code) with { PadIndex = padIndex },
		ControlKind.PadStick => (Code == 0 ? Pad.LeftStick : Pad.RightStick) with { PadIndex = padIndex },
		_ => Pad.Triggers with { PadIndex = padIndex },
	};
}

/// <summary>Breaking bindings into their controls, and naming controls as the device in hand labels them.</summary>
public static class InputBindingDescription
{
	/// <summary>The controls <paramref name="binding"/> reads, in order: one for a key, button or axis; a composite's keys
	/// (negative then positive; up, down, left, right); a stick; the trigger pair.</summary>
	public static InputControl[] Controls(this InputBinding binding)
	{
		InputControl One(int code) => binding.Family switch
		{
			InputFamily.Keyboard => new(ControlKind.Key, code),
			InputFamily.Mouse => new(ControlKind.MouseButton, code),
			_ => new(ControlKind.PadButton, code),
		};
		return binding.Kind switch
		{
			BindingKind.Button => [One(binding.Code)],
			BindingKind.Axis => [new(ControlKind.PadAxis, binding.Code)],
			BindingKind.MouseWheelY => [new(ControlKind.MouseWheel, 0)],
			BindingKind.MouseWheelX => [new(ControlKind.MouseWheel, 1)],
			BindingKind.Triggers => [new(ControlKind.PadTriggers, 0)],
			BindingKind.Composite1D => [One(binding.Code), One(binding.Code2)],
			BindingKind.Composite2D => [One(binding.Code), One(binding.Code2), One(binding.Code3), One(binding.Code4)],
			BindingKind.Stick => [new(ControlKind.PadStick, binding.Code == (int)GamepadAxis.RightX ? 1 : 0)],
			_ => [],
		};
	}

	/// <summary>
	/// The control's name as the device labels it: a key's ("Space", "W", "Left"), a mouse button's, and a pad's as a pad
	/// of <paramref name="pad"/>'s type prints it ("Cross" and "R2" on a PlayStation pad, "A" and "RT" on an Xbox pad,
	/// "B" for South on a Nintendo pad, "Left grip" on a Steam Controller).
	/// </summary>
	public static string Name(this InputControl control, GamepadType pad = GamepadType.Unknown) => control.Kind switch
	{
		ControlKind.Key => KeyName((Key)control.Code),
		ControlKind.MouseButton => (MouseButton)control.Code switch
		{
			MouseButton.Left => "Left click",
			MouseButton.Right => "Right click",
			MouseButton.Middle => "Middle click",
			var b => b.ToString(),
		},
		ControlKind.MouseWheel => control.Code == 0 ? "Wheel" : "Wheel sideways",
		ControlKind.PadButton => ButtonName((GamepadButton)control.Code, pad),
		ControlKind.PadAxis => AxisName((GamepadAxis)control.Code, control.Sign, pad),
		ControlKind.PadStick => control.Code == 0 ? "Left stick" : "Right stick",
		ControlKind.PadTriggers => $"{AxisName(GamepadAxis.LeftTrigger, 0, pad)}/{AxisName(GamepadAxis.RightTrigger, 0, pad)}",
		_ => "",
	};

	/// <summary>A key's name as printed on it, or as a player would say it.</summary>
	public static string KeyName(Key key) => key switch
	{
		>= Key.Digit0 and <= Key.Digit9 => ((char)('0' + (key - Key.Digit0))).ToString(),
		>= Key.Keypad0 and <= Key.Keypad9 => $"Num {(char)('0' + (key - Key.Keypad0))}",
		Key.LeftShift or Key.RightShift => "Shift",
		Key.LeftControl or Key.RightControl => "Ctrl",
		Key.LeftAlt or Key.RightAlt => "Alt",
		Key.LeftSuper or Key.RightSuper => "Win",
		Key.Escape => "Esc",
		Key.Backspace => "Backspace",
		Key.PageUp => "Page Up",
		Key.PageDown => "Page Down",
		Key.CapsLock => "Caps Lock",
		Key.Minus => "-",
		Key.Equal => "=",
		Key.LeftBracket => "[",
		Key.RightBracket => "]",
		Key.Backslash => "\\",
		Key.Semicolon => ";",
		Key.Apostrophe => "'",
		Key.Grave => "`",
		Key.Comma => ",",
		Key.Period => ".",
		Key.Slash => "/",
		Key.KeypadEnter => "Num Enter",
		Key.KeypadAdd => "Num +",
		Key.KeypadSubtract => "Num -",
		Key.KeypadMultiply => "Num *",
		Key.KeypadDivide => "Num /",
		Key.KeypadDecimal => "Num .",
		_ => key.ToString(),
	};

	/// <summary>A pad button's name as a pad of <paramref name="pad"/>'s type prints it.</summary>
	public static string ButtonName(GamepadButton button, GamepadType pad = GamepadType.Unknown)
	{
		var family = pad.Family();
		return button switch
		{
			GamepadButton.South => Face(family, "A", "Cross", "B"),
			GamepadButton.East => Face(family, "B", "Circle", "A"),
			GamepadButton.West => Face(family, "X", "Square", "Y"),
			GamepadButton.North => Face(family, "Y", "Triangle", "X"),
			GamepadButton.LeftBumper => Face(family, "LB", "L1", "L"),
			GamepadButton.RightBumper => Face(family, "RB", "R1", "R"),
			GamepadButton.Back => pad switch
			{
				GamepadType.XboxOne => "View",
				GamepadType.PS5 => "Create",
				GamepadType.PS4 => "Share",
				GamepadType.PS3 => "Select",
				_ when family == GamepadFamily.Nintendo => "−",
				_ => "Back",
			},
			GamepadButton.Start => pad switch
			{
				GamepadType.XboxOne => "Menu",
				GamepadType.PS4 or GamepadType.PS5 => "Options",
				_ when family == GamepadFamily.Nintendo => "+",
				_ => "Start",
			},
			GamepadButton.Guide => family switch
			{
				GamepadFamily.PlayStation => "PS",
				GamepadFamily.Nintendo => "Home",
				GamepadFamily.Steam => "Steam",
				_ => "Xbox",
			},
			GamepadButton.LeftStick => Face(family, "LS", "L3", "L stick"),
			GamepadButton.RightStick => Face(family, "RS", "R3", "R stick"),
			GamepadButton.DPadUp => "D-pad up",
			GamepadButton.DPadDown => "D-pad down",
			GamepadButton.DPadLeft => "D-pad left",
			GamepadButton.DPadRight => "D-pad right",
			GamepadButton.Misc1 => pad switch
			{
				GamepadType.PS5 => "Mic",
				_ when family == GamepadFamily.Nintendo => "Capture",
				_ => "Share",
			},
			GamepadButton.LeftPaddle1 => family == GamepadFamily.Steam ? "Left grip" : "P3",
			GamepadButton.RightPaddle1 => family == GamepadFamily.Steam ? "Right grip" : "P1",
			GamepadButton.LeftPaddle2 => "P4",
			GamepadButton.RightPaddle2 => "P2",
			GamepadButton.Touchpad => "Touchpad",
			_ => button.ToString(),
		};
	}

	/// <summary>An axis's name, or one direction of it, as a pad of <paramref name="pad"/>'s type prints it.</summary>
	public static string AxisName(GamepadAxis axis, int sign = 0, GamepadType pad = GamepadType.Unknown)
	{
		var family = pad.Family();
		return axis switch
		{
			GamepadAxis.LeftTrigger => Face(family, "LT", "L2", "ZL"),
			GamepadAxis.RightTrigger => Face(family, "RT", "R2", "ZR"),
			GamepadAxis.LeftX => sign switch { < 0 => "Left stick left", > 0 => "Left stick right", _ => "Left stick" },
			GamepadAxis.LeftY => sign switch { < 0 => "Left stick up", > 0 => "Left stick down", _ => "Left stick up/down" },
			GamepadAxis.RightX => sign switch { < 0 => "Right stick left", > 0 => "Right stick right", _ => "Right stick" },
			GamepadAxis.RightY => sign switch { < 0 => "Right stick up", > 0 => "Right stick down", _ => "Right stick up/down" },
			_ => axis.ToString(),
		};
	}

	// Xbox and Steam labels, PlayStation's, Nintendo's
	private static string Face(GamepadFamily family, string xbox, string playStation, string nintendo) => family switch
	{
		GamepadFamily.PlayStation => playStation,
		GamepadFamily.Nintendo => nintendo,
		_ => xbox,
	};
}
