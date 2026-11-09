using System.Numerics;

using Loom.Input;

using Key = Loom.Input.Key;

namespace Loom.Hosting.Web;

/// <summary>
/// Translates the browser's DOM and Gamepad-API events into Loom's neutral <see cref="InputEvent"/> stream and
/// feeds a sink (the hosted app's <c>QueueInput</c>). It is the web counterpart to the desktop's Silk pump, and
/// the one place the browser's platform → neutral tables live: <c>KeyboardEvent.code</c> → <see cref="Key"/>,
/// DOM pointer/button codes → neutral, and Standard-Gamepad indices → neutral button/axis.
/// </summary>
/// <remarks>
/// <para>
/// Events come from the <c>startInputHandlers</c> shim (loom-webgpu.js), queued by <c>startHostEventQueue</c> and fed
/// in by the host at the start of each frame, in the shape of a handful of callbacks, each carrying at most three
/// values (the ceiling the source-generated JS interop marshals for a function). The
/// three-value events get their own callback — <see cref="OnPointerMove"/>, <see cref="OnGamepadButton"/>,
/// <see cref="OnGamepadAxis"/>; the rest share a discriminated low-rate channel, <see cref="OnEvent"/>; and
/// <see cref="OnKey"/> carries the physical <c>KeyboardEvent.code</c> string this side maps to a neutral key.
/// The shim coalesces pointer moves and diffs the poll-only Gamepad API once per animation frame, so a
/// high-rate device cannot flood the frame's event capacity.
/// </para>
/// <para>
/// Held keys, mouse buttons and gamepad buttons are tracked so <see cref="ReleaseAllHeld"/> can synthesize the
/// releases the browser drops when the page loses focus or is hidden — a key held across a tab switch would
/// otherwise strand down. Single-threaded by design: the host feeds it on the thread that runs the frame.
/// </para>
/// </remarks>
internal sealed class WebInputPump
{
	// Discriminator for the low-rate OnEvent channel — kept in lockstep with the kind codes in loom-webgpu.js's
	// startInputHandlers. (Pointer moves and gamepad button/axis changes ride their own dedicated callbacks
	// because they carry three payload values, one more than a discriminated call has room for.)
	private const int KindPointerButton = 1;
	private const int KindWheel = 2;
	private const int KindText = 3;
	private const int KindGamepadConnection = 4;
	private const int KindFocus = 5;

	private readonly InputSink _sink;
	private readonly Action<bool> _onFocusChanged;

	// Held-input tracking for focus-loss release synthesis. Keys index by the neutral Key value (dense, well
	// under 256); mouse buttons and per-pad gamepad buttons ride bit masks.
	private readonly bool[] _keysHeld = new bool[256];
	private uint _mouseHeld;
	private readonly uint[] _padButtonsHeld = new uint[4];
	private readonly Dictionary<int, Vector2> _touchesHeld = [];
	private readonly (GamepadType Type, ushort Vendor, ushort Product)[] _padIdentity = new (GamepadType, ushort, ushort)[4];

	public WebInputPump(InputSink sink, Action<bool> onFocusChanged)
	{
		_sink = sink ?? throw new ArgumentNullException(nameof(sink));
		_onFocusChanged = onFocusChanged ?? throw new ArgumentNullException(nameof(onFocusChanged));
	}

	/// <summary>
	/// Pointer move: <paramref name="x"/>/<paramref name="y"/> in device pixels, <paramref name="type"/> a
	/// pointer-type code (0 mouse, 1 pen, 2 touch). Marshaled from JS as an <c>Action&lt;double, double, double&gt;</c>.
	/// </summary>
	public void OnPointerMove(double x, double y, double type)
		=> _sink(InputEvent.PointerMove((float)x, (float)y, 0, ToPointerType((int)type)));

	/// <summary>
	/// A touch contact: <paramref name="code"/> is the contact's <c>PointerEvent.pointerId</c> × 8 plus its
	/// <see cref="TouchPhase"/> (began, moved, ended or cancelled), <paramref name="x"/>/<paramref name="y"/> in device
	/// pixels. The live contacts are tracked so <see cref="ReleaseAllHeld"/> can cancel them.
	/// </summary>
	public void OnTouch(double code, double x, double y)
	{
		var packed = (long)code;
		var id = (int)(packed >> 3);
		var phase = (TouchPhase)(packed & 7);
		if (phase is TouchPhase.Ended or TouchPhase.Canceled)
			_touchesHeld.Remove(id);
		else
			_touchesHeld[id] = new Vector2((float)x, (float)y);
		_sink(InputEvent.Touch(phase, id, (float)x, (float)y));
	}

	/// <summary>
	/// Gamepad button change: <paramref name="pad"/> index, <paramref name="standardIndex"/> a Standard-Gamepad
	/// button index, <paramref name="down"/> pressed (1) / released (0).
	/// </summary>
	public void OnGamepadButton(double pad, double standardIndex, double down)
	{
		if (ToGamepadButton((int)standardIndex) is not { } button)
			return;
		var p = (int)pad;
		var isDown = down != 0;
		if ((uint)p < (uint)_padButtonsHeld.Length)
		{
			if (isDown)
				_padButtonsHeld[p] |= 1u << (int)button;
			else
				_padButtonsHeld[p] &= ~(1u << (int)button);
		}
		_sink(isDown ? InputEvent.GamepadButtonDown(p, button) : InputEvent.GamepadButtonUp(p, button));
	}

	/// <summary>
	/// Gamepad axis change: <paramref name="pad"/> index, <paramref name="axisIndex"/> an axis code (0-3 sticks,
	/// 4/5 the analog triggers), <paramref name="value"/> the axis value.
	/// </summary>
	public void OnGamepadAxis(double pad, double axisIndex, double value)
	{
		if (ToGamepadAxis((int)axisIndex) is { } axis)
			_sink(InputEvent.GamepadAxisMoved((int)pad, axis, (float)value));
	}

	/// <summary>
	/// The low-rate discriminated channel — everything that fits in two payload values: pointer buttons, the
	/// wheel, typed text, gamepad hotplug and focus. <paramref name="kind"/> selects; <paramref name="a"/>/
	/// <paramref name="b"/> carry the payload. Marshaled from JS as an <c>Action&lt;double, double, double&gt;</c>.
	/// </summary>
	public void OnEvent(double kind, double a, double b)
	{
		switch ((int)kind)
		{
			case KindPointerButton:
				// a = DOM button index, b = down (1) / up (0).
				OnPointerButton((int)a, b != 0);
				break;
			case KindWheel:
				// a = horizontal delta, b = vertical delta.
				_sink(InputEvent.Wheel((float)a, (float)b));
				break;
			case KindText:
				// a = UTF-32 code point.
				_sink(InputEvent.Text((uint)a));
				break;
			case KindGamepadConnection:
				// a = pad index, b = connected (1) / disconnected (0).
				OnGamepadConnection((int)a, b != 0);
				break;
			case KindFocus:
				// a = focused (1) / blurred (0).
				OnFocus(a != 0);
				break;
		}
	}

	/// <summary>
	/// The key channel: the physical <c>KeyboardEvent.code</c> string plus its down/up state. Marshaled from JS
	/// as an <c>Action&lt;string, bool&gt;</c>. The code → neutral table lives here — the web counterpart to the
	/// desktop pump's Silk table.
	/// </summary>
	public void OnKey(string code, bool down)
	{
		var key = ToKey(code);
		if (key == Key.Unknown)
			return;
		_keysHeld[(int)key] = down;
		_sink(down ? InputEvent.KeyDown(key) : InputEvent.KeyUp(key));
	}

	/// <summary>
	/// Synthesizes a release for every input the pump currently believes is held — the key/mouse/gamepad ups the
	/// browser drops when the page loses focus or is hidden. A release for a never-pressed input is a no-op.
	/// </summary>
	public void ReleaseAllHeld()
	{
		for (var i = 0; i < _keysHeld.Length; i++)
		{
			if (!_keysHeld[i])
				continue;
			_keysHeld[i] = false;
			_sink(InputEvent.KeyUp((Key)i));
		}

		while (_mouseHeld != 0)
		{
			var bit = BitOperations.TrailingZeroCount(_mouseHeld);
			_mouseHeld &= _mouseHeld - 1;
			_sink(InputEvent.MouseButtonUp((MouseButton)bit));
		}

		foreach (var (id, at) in _touchesHeld)
			_sink(InputEvent.Touch(TouchPhase.Canceled, id, at.X, at.Y));
		_touchesHeld.Clear();

		for (var pad = 0; pad < _padButtonsHeld.Length; pad++)
		{
			var bits = _padButtonsHeld[pad];
			_padButtonsHeld[pad] = 0;
			while (bits != 0)
			{
				var bit = BitOperations.TrailingZeroCount(bits);
				bits &= bits - 1;
				_sink(InputEvent.GamepadButtonUp(pad, (GamepadButton)bit));
			}
		}
	}

	private void OnPointerButton(int domButton, bool down)
	{
		if (ToMouseButton(domButton) is not { } button)
			return;
		if (down)
			_mouseHeld |= 1u << (int)button;
		else
			_mouseHeld &= ~(1u << (int)button);
		_sink(down ? InputEvent.MouseButtonDown(button) : InputEvent.MouseButtonUp(button));
	}

	/// <summary>
	/// A pad's <c>Gamepad.id</c>, which the page sends just before the pad connects: what the pad is
	/// (<see cref="GamepadIdentity.FromBrowserId"/>), held for its connection.
	/// </summary>
	public void OnGamepadId(double pad, string id)
	{
		var p = (int)pad;
		if ((uint)p >= (uint)_padIdentity.Length)
			return;
		var type = GamepadIdentity.FromBrowserId(id, out var vendor, out var product);
		_padIdentity[p] = (type, vendor, product);
	}

	private void OnGamepadConnection(int pad, bool connected)
	{
		// A fresh (dis)connect holds nothing, so clear the pad's tracked buttons rather than stranding them.
		if ((uint)pad < (uint)_padButtonsHeld.Length)
			_padButtonsHeld[pad] = 0;
		if (!connected)
		{
			_sink(InputEvent.GamepadDisconnected(pad));
			return;
		}
		var (type, vendor, product) = (uint)pad < (uint)_padIdentity.Length ? _padIdentity[pad] : default;
		_sink(InputEvent.GamepadConnected(pad, type, vendor, product));
	}

	private void OnFocus(bool focused)
	{
		if (!focused)
			ReleaseAllHeld();
		_onFocusChanged(focused);
	}

	// The pointer-type codes the shim emits (mirrors PointerEvent.pointerType): 0 = mouse, 1 = pen, 2 = touch.
	private static PointerDeviceType ToPointerType(int type) => type switch
	{
		1 => PointerDeviceType.Pen,
		2 => PointerDeviceType.Touch,
		_ => PointerDeviceType.Mouse,
	};

	// DOM MouseEvent.button ordering (0 left, 1 middle, 2 right, 3 back, 4 forward) → neutral.
	private static MouseButton? ToMouseButton(int domButton) => domButton switch
	{
		0 => MouseButton.Left,
		1 => MouseButton.Middle,
		2 => MouseButton.Right,
		3 => MouseButton.Button4,
		4 => MouseButton.Button5,
		_ => null,
	};

	// Standard-Gamepad button indices → neutral. Indices 6 and 7 are the analog triggers, forwarded as axes
	// (LeftTrigger/RightTrigger) rather than buttons, so they are absent here.
	private static GamepadButton? ToGamepadButton(int index) => index switch
	{
		0 => GamepadButton.South,
		1 => GamepadButton.East,
		2 => GamepadButton.West,
		3 => GamepadButton.North,
		4 => GamepadButton.LeftBumper,
		5 => GamepadButton.RightBumper,
		8 => GamepadButton.Back,
		9 => GamepadButton.Start,
		10 => GamepadButton.LeftStick,
		11 => GamepadButton.RightStick,
		12 => GamepadButton.DPadUp,
		13 => GamepadButton.DPadDown,
		14 => GamepadButton.DPadLeft,
		15 => GamepadButton.DPadRight,
		16 => GamepadButton.Guide,
		17 => GamepadButton.Touchpad,   // past the standard layout: Chromium's for a PlayStation pad's touchpad click
		_ => null,
	};

	// Axis indices the shim emits: 0-3 are the two sticks (from Gamepad.axes), 4/5 the analog triggers (from the
	// Gamepad.buttons[6]/[7] values) — see startInputHandlers.
	private static GamepadAxis? ToGamepadAxis(int index) => index switch
	{
		0 => GamepadAxis.LeftX,
		1 => GamepadAxis.LeftY,
		2 => GamepadAxis.RightX,
		3 => GamepadAxis.RightY,
		4 => GamepadAxis.LeftTrigger,
		5 => GamepadAxis.RightTrigger,
		_ => null,
	};

	// The one KeyboardEvent.code → neutral-key table. `code` is the physical key independent of layout/modifiers
	// (typed characters ride the separate Text channel). Unmapped codes map to Unknown and are dropped by OnKey.
	private static Key ToKey(string code) => code switch
	{
		"KeyA" => Key.A,
		"KeyB" => Key.B,
		"KeyC" => Key.C,
		"KeyD" => Key.D,
		"KeyE" => Key.E,
		"KeyF" => Key.F,
		"KeyG" => Key.G,
		"KeyH" => Key.H,
		"KeyI" => Key.I,
		"KeyJ" => Key.J,
		"KeyK" => Key.K,
		"KeyL" => Key.L,
		"KeyM" => Key.M,
		"KeyN" => Key.N,
		"KeyO" => Key.O,
		"KeyP" => Key.P,
		"KeyQ" => Key.Q,
		"KeyR" => Key.R,
		"KeyS" => Key.S,
		"KeyT" => Key.T,
		"KeyU" => Key.U,
		"KeyV" => Key.V,
		"KeyW" => Key.W,
		"KeyX" => Key.X,
		"KeyY" => Key.Y,
		"KeyZ" => Key.Z,

		"Digit0" => Key.Digit0,
		"Digit1" => Key.Digit1,
		"Digit2" => Key.Digit2,
		"Digit3" => Key.Digit3,
		"Digit4" => Key.Digit4,
		"Digit5" => Key.Digit5,
		"Digit6" => Key.Digit6,
		"Digit7" => Key.Digit7,
		"Digit8" => Key.Digit8,
		"Digit9" => Key.Digit9,

		"Space" => Key.Space,
		"Enter" => Key.Enter,
		"Escape" => Key.Escape,
		"Tab" => Key.Tab,
		"Backspace" => Key.Backspace,
		"Insert" => Key.Insert,
		"Delete" => Key.Delete,
		"Home" => Key.Home,
		"End" => Key.End,
		"PageUp" => Key.PageUp,
		"PageDown" => Key.PageDown,

		"ArrowLeft" => Key.Left,
		"ArrowRight" => Key.Right,
		"ArrowUp" => Key.Up,
		"ArrowDown" => Key.Down,

		"ShiftLeft" => Key.LeftShift,
		"ShiftRight" => Key.RightShift,
		"ControlLeft" => Key.LeftControl,
		"ControlRight" => Key.RightControl,
		"AltLeft" => Key.LeftAlt,
		"AltRight" => Key.RightAlt,
		"MetaLeft" => Key.LeftSuper,
		"MetaRight" => Key.RightSuper,
		"ContextMenu" => Key.Menu,

		"CapsLock" => Key.CapsLock,
		"NumLock" => Key.NumLock,
		"ScrollLock" => Key.ScrollLock,
		"PrintScreen" => Key.PrintScreen,
		"Pause" => Key.Pause,

		"Minus" => Key.Minus,
		"Equal" => Key.Equal,
		"BracketLeft" => Key.LeftBracket,
		"BracketRight" => Key.RightBracket,
		"Backslash" => Key.Backslash,
		"Semicolon" => Key.Semicolon,
		"Quote" => Key.Apostrophe,
		"Backquote" => Key.Grave,
		"Comma" => Key.Comma,
		"Period" => Key.Period,
		"Slash" => Key.Slash,

		"F1" => Key.F1,
		"F2" => Key.F2,
		"F3" => Key.F3,
		"F4" => Key.F4,
		"F5" => Key.F5,
		"F6" => Key.F6,
		"F7" => Key.F7,
		"F8" => Key.F8,
		"F9" => Key.F9,
		"F10" => Key.F10,
		"F11" => Key.F11,
		"F12" => Key.F12,
		"F13" => Key.F13,
		"F14" => Key.F14,
		"F15" => Key.F15,
		"F16" => Key.F16,
		"F17" => Key.F17,
		"F18" => Key.F18,
		"F19" => Key.F19,
		"F20" => Key.F20,
		"F21" => Key.F21,
		"F22" => Key.F22,
		"F23" => Key.F23,
		"F24" => Key.F24,

		"Numpad0" => Key.Keypad0,
		"Numpad1" => Key.Keypad1,
		"Numpad2" => Key.Keypad2,
		"Numpad3" => Key.Keypad3,
		"Numpad4" => Key.Keypad4,
		"Numpad5" => Key.Keypad5,
		"Numpad6" => Key.Keypad6,
		"Numpad7" => Key.Keypad7,
		"Numpad8" => Key.Keypad8,
		"Numpad9" => Key.Keypad9,
		"NumpadDivide" => Key.KeypadDivide,
		"NumpadMultiply" => Key.KeypadMultiply,
		"NumpadSubtract" => Key.KeypadSubtract,
		"NumpadAdd" => Key.KeypadAdd,
		"NumpadDecimal" => Key.KeypadDecimal,
		"NumpadEnter" => Key.KeypadEnter,
		"NumpadEqual" => Key.KeypadEqual,

		_ => Key.Unknown,
	};
}
