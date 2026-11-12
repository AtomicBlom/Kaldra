using System.Numerics;

namespace Loom.Input;

/// <summary>
/// One neutral input event — a tagged union over every device family, discriminated by <see cref="Kind"/>. Host
/// adapters enqueue these into an <see cref="InputEventRing"/>; the layer stack folds them into per-layer
/// <see cref="InputSnapshot"/>s. Which fields are meaningful depends on <see cref="Kind"/>; use the typed
/// factory methods to construct events and the typed accessors (<see cref="Key"/>, <see cref="Button"/>, …) to
/// read them.
/// </summary>
/// <remarks>
/// <see cref="Timestamp"/> is adapter-supplied and must never enter hashed or cached state — under frozen-time
/// capture it is derived from the frame counter rather than a wall clock so runs stay deterministic.
/// </remarks>
public readonly record struct InputEvent
{
	/// <summary>Which device produced the event and what it reports.</summary>
	public InputEventKind Kind { get; init; }

	/// <summary>The device code: a <see cref="Loom.Input.Key"/>, <see cref="MouseButton"/>,
	/// <see cref="GamepadButton"/>, <see cref="GamepadAxis"/>, or <see cref="TouchPhase"/> cast to
	/// <see cref="int"/>, depending on <see cref="Kind"/>.</summary>
	public int Code { get; init; }

	/// <summary>The scalar level: <c>1</c>/<c>0</c> for button transitions, the axis value for
	/// <see cref="InputEventKind.GamepadAxis"/>.</summary>
	public float Value { get; init; }

	/// <summary>Primary coordinate: pointer/touch X, or the horizontal wheel delta.</summary>
	public float X { get; init; }

	/// <summary>Secondary coordinate: pointer/touch Y, or the vertical wheel delta.</summary>
	public float Y { get; init; }

	/// <summary>The pointer id (for pointer/touch events), the touch contact id, or the pad index for gamepad
	/// events (see <see cref="PadIndex"/>).</summary>
	public int PointerId { get; init; }

	/// <summary>The typed character for <see cref="InputEventKind.Text"/> as a UTF-32 code point.</summary>
	public uint CodePoint { get; init; }

	/// <summary>The physical device behind a pointer/touch event.</summary>
	public PointerDeviceType PointerType { get; init; }

	/// <summary>An adapter-supplied timestamp (ticks or frame-derived). Kept out of any hashed/cached state.</summary>
	public long Timestamp { get; init; }

	/// <summary>The gamepad index this event refers to (an alias of <see cref="PointerId"/> for gamepad kinds).</summary>
	public int PadIndex => PointerId;

	/// <summary>The event's device family/families, used by <see cref="InputContext.Block"/>. A mouse pointer
	/// move is both <see cref="InputDevice.Mouse"/> and <see cref="InputDevice.Pointer"/>; a touch is both
	/// <see cref="InputDevice.Touch"/> and <see cref="InputDevice.Pointer"/>.</summary>
	public InputDevice Device => Kind switch
	{
		InputEventKind.KeyDown or InputEventKind.KeyUp or InputEventKind.Text => InputDevice.Keyboard,
		InputEventKind.MouseButtonDown or InputEventKind.MouseButtonUp or InputEventKind.Wheel => InputDevice.Mouse,
		InputEventKind.PointerMove => InputDevice.Pointer | PointerType switch
		{
			PointerDeviceType.Touch => InputDevice.Touch,
			PointerDeviceType.Mouse => InputDevice.Mouse,
			_ => InputDevice.None,
		},
		InputEventKind.Touch => InputDevice.Touch | InputDevice.Pointer,
		InputEventKind.GamepadButtonDown or InputEventKind.GamepadButtonUp or InputEventKind.GamepadAxis
			or InputEventKind.GamepadConnected or InputEventKind.GamepadDisconnected => InputDevice.Gamepad,
		_ => InputDevice.None,
	};

	/// <summary>The key for a <see cref="InputEventKind.KeyDown"/>/<see cref="InputEventKind.KeyUp"/> event.</summary>
	public Key Key => (Key)Code;

	/// <summary>The button for a <see cref="InputEventKind.MouseButtonDown"/>/<see cref="InputEventKind.MouseButtonUp"/> event.</summary>
	public MouseButton Button => (MouseButton)Code;

	/// <summary>The button for a <see cref="InputEventKind.GamepadButtonDown"/>/<see cref="InputEventKind.GamepadButtonUp"/> event.</summary>
	public GamepadButton GamepadButton => (GamepadButton)Code;

	/// <summary>The axis for a <see cref="InputEventKind.GamepadAxis"/> event.</summary>
	public GamepadAxis GamepadAxis => (GamepadAxis)Code;

	/// <summary>The phase for a <see cref="InputEventKind.Touch"/> event.</summary>
	public TouchPhase Phase => (TouchPhase)Code;

	/// <summary>The pointer/touch position for a <see cref="InputEventKind.PointerMove"/>/<see cref="InputEventKind.Touch"/> event.</summary>
	public Vector2 Position => new(X, Y);

	public static InputEvent KeyDown(Key key, long timestamp = 0)
		=> new() { Kind = InputEventKind.KeyDown, Code = (int)key, Value = 1f, Timestamp = timestamp };

	public static InputEvent KeyUp(Key key, long timestamp = 0)
		=> new() { Kind = InputEventKind.KeyUp, Code = (int)key, Value = 0f, Timestamp = timestamp };

	/// <summary>A typed character as a UTF-32 code point.</summary>
	public static InputEvent Text(uint codePoint, long timestamp = 0)
		=> new() { Kind = InputEventKind.Text, CodePoint = codePoint, Timestamp = timestamp };

	public static InputEvent MouseButtonDown(MouseButton button, long timestamp = 0)
		=> new() { Kind = InputEventKind.MouseButtonDown, Code = (int)button, Value = 1f, Timestamp = timestamp };

	public static InputEvent MouseButtonUp(MouseButton button, long timestamp = 0)
		=> new() { Kind = InputEventKind.MouseButtonUp, Code = (int)button, Value = 0f, Timestamp = timestamp };

	public static InputEvent PointerMove(float x, float y, int pointerId = 0, PointerDeviceType type = PointerDeviceType.Mouse, long timestamp = 0)
		=> new() { Kind = InputEventKind.PointerMove, X = x, Y = y, PointerId = pointerId, PointerType = type, Timestamp = timestamp };

	/// <summary>A scroll-wheel delta (<paramref name="x"/> horizontal, <paramref name="y"/> vertical).</summary>
	public static InputEvent Wheel(float x, float y, long timestamp = 0)
		=> new() { Kind = InputEventKind.Wheel, X = x, Y = y, Timestamp = timestamp };

	public static InputEvent GamepadButtonDown(int padIndex, GamepadButton button, long timestamp = 0)
		=> new() { Kind = InputEventKind.GamepadButtonDown, Code = (int)button, Value = 1f, PointerId = padIndex, Timestamp = timestamp };

	public static InputEvent GamepadButtonUp(int padIndex, GamepadButton button, long timestamp = 0)
		=> new() { Kind = InputEventKind.GamepadButtonUp, Code = (int)button, Value = 0f, PointerId = padIndex, Timestamp = timestamp };

	public static InputEvent GamepadAxisMoved(int padIndex, GamepadAxis axis, float value, long timestamp = 0)
		=> new() { Kind = InputEventKind.GamepadAxis, Code = (int)axis, Value = value, PointerId = padIndex, Timestamp = timestamp };

	public static InputEvent GamepadConnected(int padIndex, long timestamp = 0)
		=> new() { Kind = InputEventKind.GamepadConnected, PointerId = padIndex, Timestamp = timestamp };

	/// <summary>A pad connected, with what it is: its <paramref name="type"/> in <see cref="Code"/>, and its USB
	/// <paramref name="vendor"/> and <paramref name="product"/> ids in <see cref="CodePoint"/> (vendor in the high half),
	/// zero where the host can't say.</summary>
	public static InputEvent GamepadConnected(int padIndex, GamepadType type, ushort vendor, ushort product, long timestamp = 0)
		=> new()
		{
			Kind = InputEventKind.GamepadConnected,
			PointerId = padIndex,
			Code = (int)type,
			CodePoint = ((uint)vendor << 16) | product,
			Timestamp = timestamp,
		};

	public static InputEvent GamepadDisconnected(int padIndex, long timestamp = 0)
		=> new() { Kind = InputEventKind.GamepadDisconnected, PointerId = padIndex, Timestamp = timestamp };

	public static InputEvent Touch(TouchPhase phase, int touchId, float x, float y, long timestamp = 0)
		=> new()
		{
			Kind = InputEventKind.Touch,
			Code = (int)phase,
			X = x,
			Y = y,
			PointerId = touchId,
			PointerType = PointerDeviceType.Touch,
			Timestamp = timestamp,
		};
}
