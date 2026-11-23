using System.Numerics;

using BinaryVibrance.Kaldra.Core.Hosting;
using BinaryVibrance.Kaldra.Input;

using Silk.NET.Input;

using KaldraKey = BinaryVibrance.Kaldra.Input.Key;
using KaldraMouseButton = BinaryVibrance.Kaldra.Input.MouseButton;
using SilkKey = Silk.NET.Input.Key;
using SilkMouseButton = Silk.NET.Input.MouseButton;

namespace BinaryVibrance.Kaldra.Hosting.Desktop;

/// <summary>
/// Translates a Silk.NET <see cref="IInputContext"/> into Kaldra's neutral <see cref="InputEvent"/> stream and feeds
/// it to a sink (an app's <c>QueueInput</c>, or any hand-rolled host's own queue). It is the one place the
/// Silk-key → <see cref="KaldraKey"/> table lives. Reusable: it takes the input context rather than the window, so a
/// host that owns its own window can create the context and hand it here.
/// </summary>
/// <remarks>
/// <para>
/// Consecutive pointer moves are coalesced to a single event per frame so a high-rate mouse cannot flood the
/// frame's event capacity: a move is held pending and flushed either when a different event arrives (preserving
/// order) or once per frame by <see cref="Update"/>.
/// </para>
/// <para>
/// The pump tracks which inputs are held so <see cref="ReleaseAllHeld"/> can synthesize the matching releases when
/// the window loses focus — the OS stops delivering the key/button ups for anything held across an alt-tab, which
/// would otherwise strand it down. Gamepad hotplug is handled through the context's
/// <see cref="IInputContext.ConnectionChanged"/>, and queued rumble is drained to the pads' vibration motors each
/// frame (a no-op where unsupported). Single-threaded by design — Silk events fire on the pump thread.
/// </para>
/// </remarks>
public sealed class DesktopInputPump : IDisposable
{
	private readonly IInputContext _context;
	private readonly InputSink _sink;
	private readonly RumbleQueue? _rumble;
	private readonly bool _readGamepads;

	private readonly List<IKeyboard> _keyboards = [];
	private readonly List<IMouse> _mice = [];
	private readonly List<IGamepad> _gamepads = [];

	// Held-input tracking for focus-loss release synthesis. Keys are indexed by the neutral Key value (dense,
	// well under 256); mouse buttons and per-pad gamepad buttons ride bit masks.
	private readonly bool[] _keysHeld = new bool[256];
	private uint _mouseHeld;
	private readonly uint[] _padButtonsHeld = new uint[4];

	// Coalesced pointer move held until the next non-move event or the per-frame flush.
	private Vector2 _pendingMove;
	private bool _hasPendingMove;

	// Half of a surrogate pair awaiting its partner so text is delivered as a single UTF-32 code point.
	private char? _pendingHighSurrogate;

	// Remaining rumble seconds per pad, so a timed effect stops itself once elapsed.
	private readonly float[] _rumbleRemaining = new float[4];

	/// <summary>Creates a pump that feeds a hosted application's input queue and, if it reads the pads, drains its rumble
	/// output. <paramref name="gamepads"/> false leaves the pads to another source (<see cref="Sdl.SdlGamepadPump"/>), so
	/// none is seen twice.</summary>
	public DesktopInputPump(IInputContext context, IHostedApplication application, bool gamepads = true)
		: this(context, (application ?? throw new ArgumentNullException(nameof(application))).QueueInput,
			gamepads ? application.Rumble : null, gamepads)
	{
	}

	/// <summary>Creates a pump that feeds an arbitrary <paramref name="sink"/>, optionally draining
	/// <paramref name="rumble"/> to hardware; <paramref name="gamepads"/> false reads the keyboards and mice alone.</summary>
	public DesktopInputPump(IInputContext context, InputSink sink, RumbleQueue? rumble = null, bool gamepads = true)
	{
		_context = context ?? throw new ArgumentNullException(nameof(context));
		_sink = sink ?? throw new ArgumentNullException(nameof(sink));
		_rumble = rumble;
		_readGamepads = gamepads;

		_context.ConnectionChanged += OnConnectionChanged;

		foreach (var keyboard in _context.Keyboards)
			Subscribe(keyboard);
		foreach (var mouse in _context.Mice)
			Subscribe(mouse);
		foreach (var gamepad in _readGamepads ? _context.Gamepads : [])
		{
			Subscribe(gamepad);
			if (gamepad.IsConnected)
				Queue(Connected(gamepad));
		}
	}

	/// <summary>
	/// Per-frame maintenance: flush the pending coalesced pointer move into the sink, then drain queued rumble to
	/// the pads and decay any active effect by <paramref name="deltaSeconds"/>. Call once at the top of each frame,
	/// before the app resolves its input.
	/// </summary>
	public void Update(double deltaSeconds)
	{
		FlushPendingMove();
		DrainRumble(deltaSeconds);
	}

	/// <summary>
	/// Synthesizes a release for every input the pump currently believes is held — the key/mouse/gamepad ups the
	/// OS drops when the window loses focus. Call on focus loss so nothing is left stranded down.
	/// </summary>
	public void ReleaseAllHeld()
	{
		FlushPendingMove();

		for (var i = 0; i < _keysHeld.Length; i++)
		{
			if (!_keysHeld[i])
				continue;
			_keysHeld[i] = false;
			Queue(InputEvent.KeyUp((KaldraKey)i));
		}

		while (_mouseHeld != 0)
		{
			var bit = System.Numerics.BitOperations.TrailingZeroCount(_mouseHeld);
			_mouseHeld &= _mouseHeld - 1;
			Queue(InputEvent.MouseButtonUp((KaldraMouseButton)bit));
		}

		for (var pad = 0; pad < _padButtonsHeld.Length; pad++)
		{
			var bits = _padButtonsHeld[pad];
			_padButtonsHeld[pad] = 0;
			while (bits != 0)
			{
				var bit = System.Numerics.BitOperations.TrailingZeroCount(bits);
				bits &= bits - 1;
				Queue(InputEvent.GamepadButtonUp(pad, (GamepadButton)bit));
			}
		}
	}

	public void Dispose()
	{
		_context.ConnectionChanged -= OnConnectionChanged;

		foreach (var keyboard in _keyboards)
			Unsubscribe(keyboard);
		foreach (var mouse in _mice)
			Unsubscribe(mouse);
		foreach (var gamepad in _gamepads)
			Unsubscribe(gamepad);

		_keyboards.Clear();
		_mice.Clear();
		_gamepads.Clear();
	}

	private void Queue(in InputEvent e) => _sink(in e);

	// GLFW gives a pad its mapping's name ("PS5 Controller", "Xbox Controller"), and Silk.NET no USB ids: the type is
	// read from the name
	private static InputEvent Connected(IGamepad gamepad)
		=> InputEvent.GamepadConnected(gamepad.Index, GamepadIdentity.FromName(gamepad.Name), 0, 0);

	private void FlushPendingMove()
	{
		if (!_hasPendingMove)
			return;
		_hasPendingMove = false;
		Queue(InputEvent.PointerMove(_pendingMove.X, _pendingMove.Y, 0, PointerDeviceType.Mouse));
	}

	private void OnConnectionChanged(IInputDevice device, bool connected)
	{
		switch (device)
		{
			case IKeyboard keyboard when connected:
				Subscribe(keyboard);
				break;
			case IKeyboard keyboard:
				Unsubscribe(keyboard);
				_keyboards.Remove(keyboard);
				break;
			case IMouse mouse when connected:
				Subscribe(mouse);
				break;
			case IMouse mouse:
				Unsubscribe(mouse);
				_mice.Remove(mouse);
				break;
			case IGamepad when !_readGamepads:
				break;
			case IGamepad gamepad when connected:
				Subscribe(gamepad);
				Queue(Connected(gamepad));
				break;
			case IGamepad gamepad:
				Unsubscribe(gamepad);
				_gamepads.Remove(gamepad);
				Queue(InputEvent.GamepadDisconnected(gamepad.Index));
				break;
		}
	}

	private void Subscribe(IKeyboard keyboard)
	{
		if (_keyboards.Contains(keyboard))
			return;
		_keyboards.Add(keyboard);
		keyboard.KeyDown += OnKeyDown;
		keyboard.KeyUp += OnKeyUp;
		keyboard.KeyChar += OnKeyChar;
	}

	private void Unsubscribe(IKeyboard keyboard)
	{
		keyboard.KeyDown -= OnKeyDown;
		keyboard.KeyUp -= OnKeyUp;
		keyboard.KeyChar -= OnKeyChar;
	}

	private void Subscribe(IMouse mouse)
	{
		if (_mice.Contains(mouse))
			return;
		_mice.Add(mouse);
		mouse.MouseMove += OnMouseMove;
		mouse.MouseDown += OnMouseDown;
		mouse.MouseUp += OnMouseUp;
		mouse.Scroll += OnScroll;
	}

	private void Unsubscribe(IMouse mouse)
	{
		mouse.MouseMove -= OnMouseMove;
		mouse.MouseDown -= OnMouseDown;
		mouse.MouseUp -= OnMouseUp;
		mouse.Scroll -= OnScroll;
	}

	private void Subscribe(IGamepad gamepad)
	{
		if (_gamepads.Contains(gamepad))
			return;
		_gamepads.Add(gamepad);
		gamepad.ButtonDown += OnGamepadButtonDown;
		gamepad.ButtonUp += OnGamepadButtonUp;
		gamepad.ThumbstickMoved += OnThumbstickMoved;
		gamepad.TriggerMoved += OnTriggerMoved;
	}

	private void Unsubscribe(IGamepad gamepad)
	{
		gamepad.ButtonDown -= OnGamepadButtonDown;
		gamepad.ButtonUp -= OnGamepadButtonUp;
		gamepad.ThumbstickMoved -= OnThumbstickMoved;
		gamepad.TriggerMoved -= OnTriggerMoved;
	}

	private void OnKeyDown(IKeyboard keyboard, SilkKey key, int scancode)
	{
		var mapped = ToKaldraKey(key);
		if (mapped == KaldraKey.Unknown)
			return;
		FlushPendingMove();
		_keysHeld[(int)mapped] = true;
		Queue(InputEvent.KeyDown(mapped));
	}

	private void OnKeyUp(IKeyboard keyboard, SilkKey key, int scancode)
	{
		var mapped = ToKaldraKey(key);
		if (mapped == KaldraKey.Unknown)
			return;
		FlushPendingMove();
		_keysHeld[(int)mapped] = false;
		Queue(InputEvent.KeyUp(mapped));
	}

	private void OnKeyChar(IKeyboard keyboard, char ch)
	{
		FlushPendingMove();

		if (char.IsHighSurrogate(ch))
		{
			_pendingHighSurrogate = ch;
			return;
		}

		uint codePoint;
		if (char.IsLowSurrogate(ch) && _pendingHighSurrogate is { } high)
			codePoint = (uint)char.ConvertToUtf32(high, ch);
		else
			codePoint = ch;
		_pendingHighSurrogate = null;

		Queue(InputEvent.Text(codePoint));
	}

	private void OnMouseMove(IMouse mouse, Vector2 position)
	{
		_pendingMove = position;
		_hasPendingMove = true;
	}

	private void OnMouseDown(IMouse mouse, SilkMouseButton button)
	{
		if (ToKaldraButton(button) is not { } mapped)
			return;
		FlushPendingMove();
		_mouseHeld |= 1u << (int)mapped;
		Queue(InputEvent.MouseButtonDown(mapped));
	}

	private void OnMouseUp(IMouse mouse, SilkMouseButton button)
	{
		if (ToKaldraButton(button) is not { } mapped)
			return;
		FlushPendingMove();
		_mouseHeld &= ~(1u << (int)mapped);
		Queue(InputEvent.MouseButtonUp(mapped));
	}

	private void OnScroll(IMouse mouse, ScrollWheel wheel)
	{
		FlushPendingMove();
		Queue(InputEvent.Wheel(wheel.X, wheel.Y));
	}

	private void OnGamepadButtonDown(IGamepad gamepad, Button button)
	{
		if (ToGamepadButton(button.Name) is not { } mapped)
			return;
		FlushPendingMove();
		var pad = gamepad.Index;
		if ((uint)pad < (uint)_padButtonsHeld.Length)
			_padButtonsHeld[pad] |= 1u << (int)mapped;
		Queue(InputEvent.GamepadButtonDown(pad, mapped));
	}

	private void OnGamepadButtonUp(IGamepad gamepad, Button button)
	{
		if (ToGamepadButton(button.Name) is not { } mapped)
			return;
		FlushPendingMove();
		var pad = gamepad.Index;
		if ((uint)pad < (uint)_padButtonsHeld.Length)
			_padButtonsHeld[pad] &= ~(1u << (int)mapped);
		Queue(InputEvent.GamepadButtonUp(pad, mapped));
	}

	private void OnThumbstickMoved(IGamepad gamepad, Thumbstick thumbstick)
	{
		if (thumbstick.Index > 1)
			return;
		FlushPendingMove();
		var (xAxis, yAxis) = thumbstick.Index == 0
			? (GamepadAxis.LeftX, GamepadAxis.LeftY)
			: (GamepadAxis.RightX, GamepadAxis.RightY);
		Queue(InputEvent.GamepadAxisMoved(gamepad.Index, xAxis, thumbstick.X));
		Queue(InputEvent.GamepadAxisMoved(gamepad.Index, yAxis, thumbstick.Y));
	}

	private void OnTriggerMoved(IGamepad gamepad, Trigger trigger)
	{
		if (trigger.Index > 1)
			return;
		FlushPendingMove();
		var axis = trigger.Index == 0 ? GamepadAxis.LeftTrigger : GamepadAxis.RightTrigger;
		// GLFW reports a gamepad trigger from -1 at rest to 1 fully pressed, and Silk.NET passes that on; a GamepadAxis
		// trigger runs from 0 to 1, so the first half of its travel would otherwise read as nothing
		Queue(InputEvent.GamepadAxisMoved(gamepad.Index, axis, Math.Clamp((trigger.Position + 1f) * 0.5f, 0f, 1f)));
	}

	private void DrainRumble(double deltaSeconds)
	{
		if (_rumble is not null)
			while (_rumble.TryDequeue(out var command))
				ApplyRumble(command);

		for (var pad = 0; pad < _rumbleRemaining.Length; pad++)
		{
			if (_rumbleRemaining[pad] <= 0f)
				continue;
			_rumbleRemaining[pad] -= (float)deltaSeconds;
			if (_rumbleRemaining[pad] <= 0f)
			{
				_rumbleRemaining[pad] = 0f;
				SetMotors(pad, 0f, 0f);
			}
		}
	}

	private void ApplyRumble(in RumbleCommand command)
	{
		if ((uint)command.PadIndex >= (uint)_rumbleRemaining.Length)
			return;

		// A non-positive duration is an explicit stop; otherwise drive the motors and arm the decay timer.
		if (command.Seconds <= 0f)
		{
			_rumbleRemaining[command.PadIndex] = 0f;
			SetMotors(command.PadIndex, 0f, 0f);
			return;
		}

		SetMotors(command.PadIndex, command.LowFrequency, command.HighFrequency);
		_rumbleRemaining[command.PadIndex] = command.Seconds;
	}

	private void SetMotors(int padIndex, float low, float high)
	{
		var gamepad = FindGamepad(padIndex);
		var motors = gamepad?.VibrationMotors;
		if (motors is null || motors.Count == 0)
			return;
		motors[0].Speed = low;
		if (motors.Count > 1)
			motors[1].Speed = high;
	}

	private IGamepad? FindGamepad(int index)
	{
		foreach (var gamepad in _gamepads)
			if (gamepad.Index == index)
				return gamepad;
		return null;
	}

	private static KaldraMouseButton? ToKaldraButton(SilkMouseButton button) => button switch
	{
		SilkMouseButton.Left => KaldraMouseButton.Left,
		SilkMouseButton.Right => KaldraMouseButton.Right,
		SilkMouseButton.Middle => KaldraMouseButton.Middle,
		SilkMouseButton.Button4 => KaldraMouseButton.Button4,
		SilkMouseButton.Button5 => KaldraMouseButton.Button5,
		_ => null,
	};

	private static GamepadButton? ToGamepadButton(ButtonName name) => name switch
	{
		ButtonName.A => GamepadButton.South,
		ButtonName.B => GamepadButton.East,
		ButtonName.X => GamepadButton.West,
		ButtonName.Y => GamepadButton.North,
		ButtonName.LeftBumper => GamepadButton.LeftBumper,
		ButtonName.RightBumper => GamepadButton.RightBumper,
		ButtonName.Back => GamepadButton.Back,
		ButtonName.Start => GamepadButton.Start,
		ButtonName.Home => GamepadButton.Guide,
		ButtonName.LeftStick => GamepadButton.LeftStick,
		ButtonName.RightStick => GamepadButton.RightStick,
		ButtonName.DPadUp => GamepadButton.DPadUp,
		ButtonName.DPadDown => GamepadButton.DPadDown,
		ButtonName.DPadLeft => GamepadButton.DPadLeft,
		ButtonName.DPadRight => GamepadButton.DPadRight,
		_ => null,
	};

	// The one Silk-key → neutral-key table. Silk keys with no neutral equivalent (World1/World2, F25) map to
	// Unknown and are dropped by the caller.
	private static KaldraKey ToKaldraKey(SilkKey key) => key switch
	{
		SilkKey.A => KaldraKey.A,
		SilkKey.B => KaldraKey.B,
		SilkKey.C => KaldraKey.C,
		SilkKey.D => KaldraKey.D,
		SilkKey.E => KaldraKey.E,
		SilkKey.F => KaldraKey.F,
		SilkKey.G => KaldraKey.G,
		SilkKey.H => KaldraKey.H,
		SilkKey.I => KaldraKey.I,
		SilkKey.J => KaldraKey.J,
		SilkKey.K => KaldraKey.K,
		SilkKey.L => KaldraKey.L,
		SilkKey.M => KaldraKey.M,
		SilkKey.N => KaldraKey.N,
		SilkKey.O => KaldraKey.O,
		SilkKey.P => KaldraKey.P,
		SilkKey.Q => KaldraKey.Q,
		SilkKey.R => KaldraKey.R,
		SilkKey.S => KaldraKey.S,
		SilkKey.T => KaldraKey.T,
		SilkKey.U => KaldraKey.U,
		SilkKey.V => KaldraKey.V,
		SilkKey.W => KaldraKey.W,
		SilkKey.X => KaldraKey.X,
		SilkKey.Y => KaldraKey.Y,
		SilkKey.Z => KaldraKey.Z,

		SilkKey.Number0 => KaldraKey.Digit0,
		SilkKey.Number1 => KaldraKey.Digit1,
		SilkKey.Number2 => KaldraKey.Digit2,
		SilkKey.Number3 => KaldraKey.Digit3,
		SilkKey.Number4 => KaldraKey.Digit4,
		SilkKey.Number5 => KaldraKey.Digit5,
		SilkKey.Number6 => KaldraKey.Digit6,
		SilkKey.Number7 => KaldraKey.Digit7,
		SilkKey.Number8 => KaldraKey.Digit8,
		SilkKey.Number9 => KaldraKey.Digit9,

		SilkKey.Space => KaldraKey.Space,
		SilkKey.Enter => KaldraKey.Enter,
		SilkKey.Escape => KaldraKey.Escape,
		SilkKey.Tab => KaldraKey.Tab,
		SilkKey.Backspace => KaldraKey.Backspace,
		SilkKey.Insert => KaldraKey.Insert,
		SilkKey.Delete => KaldraKey.Delete,
		SilkKey.Home => KaldraKey.Home,
		SilkKey.End => KaldraKey.End,
		SilkKey.PageUp => KaldraKey.PageUp,
		SilkKey.PageDown => KaldraKey.PageDown,

		SilkKey.Left => KaldraKey.Left,
		SilkKey.Right => KaldraKey.Right,
		SilkKey.Up => KaldraKey.Up,
		SilkKey.Down => KaldraKey.Down,

		SilkKey.ShiftLeft => KaldraKey.LeftShift,
		SilkKey.ShiftRight => KaldraKey.RightShift,
		SilkKey.ControlLeft => KaldraKey.LeftControl,
		SilkKey.ControlRight => KaldraKey.RightControl,
		SilkKey.AltLeft => KaldraKey.LeftAlt,
		SilkKey.AltRight => KaldraKey.RightAlt,
		SilkKey.SuperLeft => KaldraKey.LeftSuper,
		SilkKey.SuperRight => KaldraKey.RightSuper,
		SilkKey.Menu => KaldraKey.Menu,

		SilkKey.CapsLock => KaldraKey.CapsLock,
		SilkKey.NumLock => KaldraKey.NumLock,
		SilkKey.ScrollLock => KaldraKey.ScrollLock,
		SilkKey.PrintScreen => KaldraKey.PrintScreen,
		SilkKey.Pause => KaldraKey.Pause,

		SilkKey.Minus => KaldraKey.Minus,
		SilkKey.Equal => KaldraKey.Equal,
		SilkKey.LeftBracket => KaldraKey.LeftBracket,
		SilkKey.RightBracket => KaldraKey.RightBracket,
		SilkKey.BackSlash => KaldraKey.Backslash,
		SilkKey.Semicolon => KaldraKey.Semicolon,
		SilkKey.Apostrophe => KaldraKey.Apostrophe,
		SilkKey.GraveAccent => KaldraKey.Grave,
		SilkKey.Comma => KaldraKey.Comma,
		SilkKey.Period => KaldraKey.Period,
		SilkKey.Slash => KaldraKey.Slash,

		SilkKey.F1 => KaldraKey.F1,
		SilkKey.F2 => KaldraKey.F2,
		SilkKey.F3 => KaldraKey.F3,
		SilkKey.F4 => KaldraKey.F4,
		SilkKey.F5 => KaldraKey.F5,
		SilkKey.F6 => KaldraKey.F6,
		SilkKey.F7 => KaldraKey.F7,
		SilkKey.F8 => KaldraKey.F8,
		SilkKey.F9 => KaldraKey.F9,
		SilkKey.F10 => KaldraKey.F10,
		SilkKey.F11 => KaldraKey.F11,
		SilkKey.F12 => KaldraKey.F12,
		SilkKey.F13 => KaldraKey.F13,
		SilkKey.F14 => KaldraKey.F14,
		SilkKey.F15 => KaldraKey.F15,
		SilkKey.F16 => KaldraKey.F16,
		SilkKey.F17 => KaldraKey.F17,
		SilkKey.F18 => KaldraKey.F18,
		SilkKey.F19 => KaldraKey.F19,
		SilkKey.F20 => KaldraKey.F20,
		SilkKey.F21 => KaldraKey.F21,
		SilkKey.F22 => KaldraKey.F22,
		SilkKey.F23 => KaldraKey.F23,
		SilkKey.F24 => KaldraKey.F24,

		SilkKey.Keypad0 => KaldraKey.Keypad0,
		SilkKey.Keypad1 => KaldraKey.Keypad1,
		SilkKey.Keypad2 => KaldraKey.Keypad2,
		SilkKey.Keypad3 => KaldraKey.Keypad3,
		SilkKey.Keypad4 => KaldraKey.Keypad4,
		SilkKey.Keypad5 => KaldraKey.Keypad5,
		SilkKey.Keypad6 => KaldraKey.Keypad6,
		SilkKey.Keypad7 => KaldraKey.Keypad7,
		SilkKey.Keypad8 => KaldraKey.Keypad8,
		SilkKey.Keypad9 => KaldraKey.Keypad9,
		SilkKey.KeypadDivide => KaldraKey.KeypadDivide,
		SilkKey.KeypadMultiply => KaldraKey.KeypadMultiply,
		SilkKey.KeypadSubtract => KaldraKey.KeypadSubtract,
		SilkKey.KeypadAdd => KaldraKey.KeypadAdd,
		SilkKey.KeypadDecimal => KaldraKey.KeypadDecimal,
		SilkKey.KeypadEnter => KaldraKey.KeypadEnter,
		SilkKey.KeypadEqual => KaldraKey.KeypadEqual,

		_ => KaldraKey.Unknown,
	};
}
