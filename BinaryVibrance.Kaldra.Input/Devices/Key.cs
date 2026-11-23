namespace BinaryVibrance.Kaldra.Input;

/// <summary>
/// A neutral keyboard key — Kaldra's own identity for a physical key, independent of any platform or windowing
/// library. Host adapters map their native key codes onto this enum once. Values are contiguous from zero so a
/// key doubles as an index into the snapshot's held-key bitset; keep them dense when adding entries.
/// </summary>
public enum Key
{
	Unknown = 0,

	A,
	B,
	C,
	D,
	E,
	F,
	G,
	H,
	I,
	J,
	K,
	L,
	M,
	N,
	O,
	P,
	Q,
	R,
	S,
	T,
	U,
	V,
	W,
	X,
	Y,
	Z,

	/// <summary>The <c>0</c> key on the number row.</summary>
	Digit0,
	Digit1,
	Digit2,
	Digit3,
	Digit4,
	Digit5,
	Digit6,
	Digit7,
	Digit8,
	Digit9,

	Space,
	Enter,
	Escape,
	Tab,
	Backspace,
	Insert,
	Delete,
	Home,
	End,
	PageUp,
	PageDown,

	Left,
	Right,
	Up,
	Down,

	LeftShift,
	RightShift,
	LeftControl,
	RightControl,
	LeftAlt,
	RightAlt,

	/// <summary>The left OS/meta/command key (Windows key, macOS Command).</summary>
	LeftSuper,
	RightSuper,

	/// <summary>The context-menu / application key.</summary>
	Menu,

	CapsLock,
	NumLock,
	ScrollLock,
	PrintScreen,
	Pause,

	/// <summary>The <c>-</c> / <c>_</c> key.</summary>
	Minus,

	/// <summary>The <c>=</c> / <c>+</c> key.</summary>
	Equal,

	/// <summary>The <c>[</c> / <c>{</c> key.</summary>
	LeftBracket,

	/// <summary>The <c>]</c> / <c>}</c> key.</summary>
	RightBracket,

	/// <summary>The <c>\</c> / <c>|</c> key.</summary>
	Backslash,

	/// <summary>The <c>;</c> / <c>:</c> key.</summary>
	Semicolon,

	/// <summary>The <c>'</c> / <c>"</c> key.</summary>
	Apostrophe,

	/// <summary>The <c>`</c> / <c>~</c> key.</summary>
	Grave,

	/// <summary>The <c>,</c> / <c>&lt;</c> key.</summary>
	Comma,

	/// <summary>The <c>.</c> / <c>&gt;</c> key.</summary>
	Period,

	/// <summary>The <c>/</c> / <c>?</c> key.</summary>
	Slash,

	F1,
	F2,
	F3,
	F4,
	F5,
	F6,
	F7,
	F8,
	F9,
	F10,
	F11,
	F12,
	F13,
	F14,
	F15,
	F16,
	F17,
	F18,
	F19,
	F20,
	F21,
	F22,
	F23,
	F24,

	Keypad0,
	Keypad1,
	Keypad2,
	Keypad3,
	Keypad4,
	Keypad5,
	Keypad6,
	Keypad7,
	Keypad8,
	Keypad9,
	KeypadDivide,
	KeypadMultiply,
	KeypadSubtract,
	KeypadAdd,
	KeypadDecimal,
	KeypadEnter,
	KeypadEqual,
}
