using Android.Views;

using BinaryVibrance.Kaldra.Input;

namespace BinaryVibrance.Kaldra.Hosting.Android;

/// <summary>
/// The Android → neutral tables: a <see cref="Keycode"/> to a <see cref="Key"/> for a keyboard, or to a
/// <see cref="GamepadButton"/> for a controller. The Android counterpart to the desktop pump's Silk table and the web
/// pump's <c>KeyboardEvent.code</c> table. Keycodes name physical keys, so typed characters ride the separate text
/// channel.
/// </summary>
internal static class AndroidKeyMap
{
	/// <summary>The neutral key for a keyboard keycode, or <see cref="Key.Unknown"/> for one with no equivalent.</summary>
	public static Key ToKey(Keycode code) => code switch
	{
		>= Keycode.A and <= Keycode.Z => Key.A + (code - Keycode.A),
		>= Keycode.Num0 and <= Keycode.Num9 => Key.Digit0 + (code - Keycode.Num0),
		>= Keycode.F1 and <= Keycode.F12 => Key.F1 + (code - Keycode.F1),
		>= Keycode.Numpad0 and <= Keycode.Numpad9 => Key.Keypad0 + (code - Keycode.Numpad0),

		Keycode.Space => Key.Space,
		Keycode.Enter => Key.Enter,
		Keycode.Escape => Key.Escape,
		Keycode.Tab => Key.Tab,
		Keycode.Del => Key.Backspace, // Android's DEL is the key left of the cursor
		Keycode.ForwardDel => Key.Delete,
		Keycode.Insert => Key.Insert,
		Keycode.MoveHome => Key.Home,
		Keycode.MoveEnd => Key.End,
		Keycode.PageUp => Key.PageUp,
		Keycode.PageDown => Key.PageDown,

		Keycode.DpadLeft => Key.Left,
		Keycode.DpadRight => Key.Right,
		Keycode.DpadUp => Key.Up,
		Keycode.DpadDown => Key.Down,

		Keycode.ShiftLeft => Key.LeftShift,
		Keycode.ShiftRight => Key.RightShift,
		Keycode.CtrlLeft => Key.LeftControl,
		Keycode.CtrlRight => Key.RightControl,
		Keycode.AltLeft => Key.LeftAlt,
		Keycode.AltRight => Key.RightAlt,
		Keycode.MetaLeft => Key.LeftSuper,
		Keycode.MetaRight => Key.RightSuper,
		Keycode.Menu => Key.Menu,

		Keycode.CapsLock => Key.CapsLock,
		Keycode.NumLock => Key.NumLock,
		Keycode.ScrollLock => Key.ScrollLock,
		Keycode.Sysrq => Key.PrintScreen,
		Keycode.Break => Key.Pause,

		Keycode.Minus => Key.Minus,
		Keycode.Equals => Key.Equal,
		Keycode.LeftBracket => Key.LeftBracket,
		Keycode.RightBracket => Key.RightBracket,
		Keycode.Backslash => Key.Backslash,
		Keycode.Semicolon => Key.Semicolon,
		Keycode.Apostrophe => Key.Apostrophe,
		Keycode.Grave => Key.Grave,
		Keycode.Comma => Key.Comma,
		Keycode.Period => Key.Period,
		Keycode.Slash => Key.Slash,

		Keycode.NumpadDivide => Key.KeypadDivide,
		Keycode.NumpadMultiply => Key.KeypadMultiply,
		Keycode.NumpadSubtract => Key.KeypadSubtract,
		Keycode.NumpadAdd => Key.KeypadAdd,
		Keycode.NumpadDot => Key.KeypadDecimal,
		Keycode.NumpadEnter => Key.KeypadEnter,
		Keycode.NumpadEquals => Key.KeypadEqual,

		_ => Key.Unknown,
	};

	/// <summary>
	/// The neutral button for a controller keycode, or null for one that is not a controller button. The face buttons
	/// are positional, as everywhere in Kaldra: <c>BUTTON_A</c> is the bottom one whatever it is labelled (a PlayStation
	/// controller's cross). The analog triggers' keycodes are left out: the triggers arrive as axes.
	/// </summary>
	public static GamepadButton? ToGamepadButton(Keycode code) => code switch
	{
		Keycode.ButtonA => GamepadButton.South,
		Keycode.ButtonB => GamepadButton.East,
		Keycode.ButtonX => GamepadButton.West,
		Keycode.ButtonY => GamepadButton.North,
		Keycode.ButtonL1 => GamepadButton.LeftBumper,
		Keycode.ButtonR1 => GamepadButton.RightBumper,
		Keycode.ButtonSelect => GamepadButton.Back,
		Keycode.ButtonStart => GamepadButton.Start,
		Keycode.ButtonMode => GamepadButton.Guide,
		Keycode.ButtonThumbl => GamepadButton.LeftStick,
		Keycode.ButtonThumbr => GamepadButton.RightStick,
		Keycode.DpadUp => GamepadButton.DPadUp,
		Keycode.DpadDown => GamepadButton.DPadDown,
		Keycode.DpadLeft => GamepadButton.DPadLeft,
		Keycode.DpadRight => GamepadButton.DPadRight,
		_ => null,
	};
}
