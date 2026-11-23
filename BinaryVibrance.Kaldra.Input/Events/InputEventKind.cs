namespace BinaryVibrance.Kaldra.Input;

/// <summary>
/// The discriminator for <see cref="InputEvent"/>'s tagged union — which device produced the event and what it
/// reports. Device transitions (<c>*Down</c>/<c>*Up</c>) fire once at the moment they happen; level state is
/// reconstructed by folding the stream into an <see cref="InputSnapshot"/>.
/// </summary>
public enum InputEventKind : byte
{
	None = 0,

	KeyDown,
	KeyUp,

	/// <summary>A typed character as a UTF-32 code point (<see cref="InputEvent.CodePoint"/>). Distinct from
	/// <see cref="KeyDown"/> because text editing needs the composed character (layout, shift, IME), which key
	/// codes cannot reproduce.</summary>
	Text,

	MouseButtonDown,
	MouseButtonUp,

	/// <summary>A pointer moved to an absolute position (<see cref="InputEvent.X"/>/<see cref="InputEvent.Y"/>).</summary>
	PointerMove,

	/// <summary>A scroll-wheel delta this event (<see cref="InputEvent.X"/> horizontal, <see cref="InputEvent.Y"/> vertical).</summary>
	Wheel,

	GamepadButtonDown,
	GamepadButtonUp,

	/// <summary>A gamepad analog axis moved to a new level (<see cref="InputEvent.Value"/>).</summary>
	GamepadAxis,

	/// <summary>A gamepad was plugged in at <see cref="InputEvent.PadIndex"/>.</summary>
	GamepadConnected,

	/// <summary>A gamepad was unplugged at <see cref="InputEvent.PadIndex"/>.</summary>
	GamepadDisconnected,

	/// <summary>A touch contact changed (<see cref="InputEvent.Phase"/> carries the lifecycle phase).</summary>
	Touch,
}
