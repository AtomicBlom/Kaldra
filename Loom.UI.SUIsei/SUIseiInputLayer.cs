using System.Numerics;
using System.Text;
using Loom.Input;
using SUIsei;
using Sui = global::SUIsei.Input;

namespace Loom.UI.SUIsei;

/// <summary>
/// The input half of the SUIsei integration — the twin of the render-only <see cref="UIPainter"/>. An
/// <see cref="IInputLayer"/> that forwards a frame's neutral input into the <see cref="UIHost"/> it feeds, then
/// blocks lower (game) layers from the pointer/keyboard when the host reported it wants them. It owns the neutral
/// <see cref="Key"/> → <see cref="Sui.VirtualKey"/> mapping table — the second key table in the input stack (the
/// desktop adapter owns the platform → neutral one). Push it above the game's layers and below any modal that must
/// keep seeing input while the UI is focused.
/// </summary>
/// <remarks>
/// The app drives <see cref="UIHost.Tick"/> itself each frame — that is what drains the queued events and refreshes
/// the capture report — so this layer only queues events and blocks on the host's <b>last</b> tick's
/// <see cref="UIHost.WantsPointer"/>/<see cref="UIHost.WantsKeyboard"/>. That is a deliberate one-frame coarse
/// latency (the same tradeoff ImGui's own report makes): the very first click onto a UI element is not yet blocked
/// and can "click through" to the game on that frame, after which the block engages.
/// </remarks>
public sealed class SUIseiInputLayer : IInputLayer
{
	private readonly UIHost _host;

	// Running pointer/modifier state, persisted across frames: neutral button/wheel/key events carry no position or
	// modifier set of their own, so a press or scroll reuses the latest move's position and the live modifier mask.
	private Vector2 _pointerPosition;
	private Sui.PointerButtons _buttons;
	private Sui.ModifierKeys _modifiers;

	// A pad through the UI (PadNavigation): the direction held, from the d-pad or the left stick, and when it next repeats
	private const float StickOn = 0.55f, StickOff = 0.35f;
	private const long RepeatFirstMs = 400, RepeatMs = 90;
	private Sui.VirtualKey _dpadHeld, _stickHeld;
	private Vector2 _stick;
	private long _repeatAt;

	/// <summary>Creates a layer that feeds <paramref name="host"/>.</summary>
	public SUIseiInputLayer(UIHost host)
	{
		ArgumentNullException.ThrowIfNull(host);
		_host = host;
	}

	/// <summary>
	/// Whether the UI has the pad: while true, any pad's face buttons go to the UI as <see cref="Sui.VirtualKey.GamepadA"/>
	/// and the rest (Start and Back as GamepadMenu and GamepadView), and its d-pad and left stick, and the keyboard's arrows,
	/// as the d-pad's keys (moving focus by position), a held direction repeating after 0.4 s every 0.09 s; and the pad is
	/// blocked from the layers below, so a press isn't also the game's.
	/// The app sets it while a menu or page that a pad can work is open.
	/// </summary>
	public bool PadNavigation
	{
		get;
		set
		{
			if (field == value)
				return;
			field = value;
			ReleasePad();   // nothing held carries across: a direction or button held as it changes is let go
		}
	}

	/// <inheritdoc/>
	public void Process(ref InputContext ctx)
	{
		foreach (ref readonly var e in ctx.Incoming)
		{
			if (PadNavigation && e.Device == InputDevice.Gamepad)
			{
				PadToUi(in e);
				continue;
			}
			switch (e.Kind)
			{
				case InputEventKind.PointerMove:
					_pointerPosition = new Vector2(e.X, e.Y);
					_host.QueuePointer(Sui.PointerInput.Move(_pointerPosition, _buttons, (uint)e.PointerId, ToDevice(e.PointerType)));
					break;

				case InputEventKind.MouseButtonDown:
					{
						_buttons |= ToPointerButton(e.Button);
						var kind = PressKind(e.Button);
						if (kind != Sui.PointerUpdateKind.None)
							_host.QueuePointer(Sui.PointerInput.Press(_pointerPosition, kind, _buttons));
						break;
					}

				case InputEventKind.MouseButtonUp:
					{
						_buttons &= ~ToPointerButton(e.Button);
						var kind = ReleaseKind(e.Button);
						if (kind != Sui.PointerUpdateKind.None)
							_host.QueuePointer(Sui.PointerInput.Release(_pointerPosition, kind, _buttons));
						break;
					}

				case InputEventKind.Wheel:
					_host.QueuePointer(Sui.PointerInput.Scroll(_pointerPosition, new Vector2(e.X, e.Y), _buttons));
					break;

				// The arrows move focus as the d-pad does while the UI has navigation (the same key, so the same rules)
				case InputEventKind.KeyDown or InputEventKind.KeyUp when PadNavigation && ArrowAsDPad(e.Key) is { } arrow:
					if (e.Kind == InputEventKind.KeyDown)
						Hold(ref _dpadHeld, arrow);
					else if (_dpadHeld == arrow)
						Hold(ref _dpadHeld, Sui.VirtualKey.None);
					break;

				case InputEventKind.KeyDown:
					{
						_modifiers |= ToModifier(e.Key);
						var vk = ToVirtualKey(e.Key);
						if (vk != Sui.VirtualKey.None)
							_host.QueueKey(new Sui.KeyInput(vk, Down: true, Repeat: false, _modifiers, 0));
						break;
					}

				case InputEventKind.KeyUp:
					{
						_modifiers &= ~ToModifier(e.Key);
						var vk = ToVirtualKey(e.Key);
						if (vk != Sui.VirtualKey.None)
							_host.QueueKey(new Sui.KeyInput(vk, Down: false, Repeat: false, _modifiers, 0));
						break;
					}

				case InputEventKind.Text:
					QueueText(e.CodePoint);
					break;
			}
		}

		// Block downstream on the host's last-tick capture report (see the type's remarks on the one-frame latency).
		// Pointer covers pen/touch moves as well as the mouse; Mouse additionally covers button/wheel events.
		if (_host.WantsPointer)
			ctx.Block(InputDevice.Pointer | InputDevice.Mouse);
		if (_host.WantsKeyboard)
			ctx.Block(InputDevice.Keyboard);
		if (PadNavigation)
		{
			RepeatHeld();
			ctx.Block(InputDevice.Gamepad);
		}
	}

	// A pad's event as the UI's: face buttons as the gamepad's keys, the d-pad as its direction keys, the left stick past
	// half way as the d-pad (back under a third, let go)
	private void PadToUi(in InputEvent e)
	{
		switch (e.Kind)
		{
			case InputEventKind.GamepadButtonDown or InputEventKind.GamepadButtonUp:
				{
					bool down = e.Kind == InputEventKind.GamepadButtonDown;
					var key = (GamepadButton)e.Code switch
					{
						GamepadButton.South => Sui.VirtualKey.GamepadA,
						GamepadButton.East => Sui.VirtualKey.GamepadB,
						GamepadButton.West => Sui.VirtualKey.GamepadX,
						GamepadButton.North => Sui.VirtualKey.GamepadY,
						GamepadButton.Start => Sui.VirtualKey.GamepadMenu,
						GamepadButton.Back => Sui.VirtualKey.GamepadView,
						GamepadButton.DPadUp => Sui.VirtualKey.GamepadDPadUp,
						GamepadButton.DPadDown => Sui.VirtualKey.GamepadDPadDown,
						GamepadButton.DPadLeft => Sui.VirtualKey.GamepadDPadLeft,
						GamepadButton.DPadRight => Sui.VirtualKey.GamepadDPadRight,
						_ => Sui.VirtualKey.None,
					};
					if (key == Sui.VirtualKey.None)
						break;
					if (IsDirection(key))
					{
						if (down)
							Hold(ref _dpadHeld, key);
						else if (_dpadHeld == key)
							Hold(ref _dpadHeld, Sui.VirtualKey.None);
					}
					else
						QueuePadKey(key, down, repeat: false);
					break;
				}
			case InputEventKind.GamepadAxis when (GamepadAxis)e.Code is GamepadAxis.LeftX or GamepadAxis.LeftY:
				{
					if ((GamepadAxis)e.Code == GamepadAxis.LeftX) _stick.X = e.Value;
					else _stick.Y = e.Value;
					var dir = _stickHeld;
					float along = dir switch
					{
						Sui.VirtualKey.GamepadDPadLeft => -_stick.X,
						Sui.VirtualKey.GamepadDPadRight => _stick.X,
						Sui.VirtualKey.GamepadDPadUp => -_stick.Y,
						Sui.VirtualKey.GamepadDPadDown => _stick.Y,
						_ => 0f,
					};
					if (dir != Sui.VirtualKey.None && along > StickOff)
						break;   // still held that way
					var next = Sui.VirtualKey.None;
					if (MathF.Max(MathF.Abs(_stick.X), MathF.Abs(_stick.Y)) > StickOn)
						next = MathF.Abs(_stick.X) > MathF.Abs(_stick.Y)
							? _stick.X < 0f ? Sui.VirtualKey.GamepadDPadLeft : Sui.VirtualKey.GamepadDPadRight
							: _stick.Y < 0f ? Sui.VirtualKey.GamepadDPadUp : Sui.VirtualKey.GamepadDPadDown;   // up is negative
					Hold(ref _stickHeld, next);
					break;
				}
		}
	}

	private static Sui.VirtualKey? ArrowAsDPad(Key key) => key switch
	{
		Key.Up => Sui.VirtualKey.GamepadDPadUp,
		Key.Down => Sui.VirtualKey.GamepadDPadDown,
		Key.Left => Sui.VirtualKey.GamepadDPadLeft,
		Key.Right => Sui.VirtualKey.GamepadDPadRight,
		_ => null,
	};

	private static bool IsDirection(Sui.VirtualKey key) => key is Sui.VirtualKey.GamepadDPadUp or Sui.VirtualKey.GamepadDPadDown
		or Sui.VirtualKey.GamepadDPadLeft or Sui.VirtualKey.GamepadDPadRight;

	// A direction pressed (or let go, for None): its key goes down at once and repeats while held
	private void Hold(ref Sui.VirtualKey held, Sui.VirtualKey key)
	{
		if (held == key)
			return;
		if (held != Sui.VirtualKey.None)
			QueuePadKey(held, down: false, repeat: false);
		held = key;
		if (key == Sui.VirtualKey.None)
			return;
		QueuePadKey(key, down: true, repeat: false);
		_repeatAt = Environment.TickCount64 + RepeatFirstMs;
	}

	private void RepeatHeld()
	{
		var held = _dpadHeld != Sui.VirtualKey.None ? _dpadHeld : _stickHeld;
		if (held == Sui.VirtualKey.None || Environment.TickCount64 < _repeatAt)
			return;
		QueuePadKey(held, down: true, repeat: true);
		_repeatAt = Environment.TickCount64 + RepeatMs;
	}

	private void ReleasePad()
	{
		Hold(ref _dpadHeld, Sui.VirtualKey.None);
		Hold(ref _stickHeld, Sui.VirtualKey.None);
		_stick = Vector2.Zero;
	}

	private void QueuePadKey(Sui.VirtualKey key, bool down, bool repeat)
		=> _host.QueueKey(new Sui.KeyInput(key, down, repeat, _modifiers, 0));

	private void QueueText(uint codePoint)
	{
		if (!Rune.TryCreate(codePoint, out var rune))
			return;
		Span<char> buffer = stackalloc char[2];
		var written = rune.EncodeToUtf16(buffer);
		_host.QueueText(buffer[..written]);
	}

	private static Sui.PointerDeviceType ToDevice(PointerDeviceType type) => type switch
	{
		PointerDeviceType.Pen => Sui.PointerDeviceType.Pen,
		PointerDeviceType.Touch => Sui.PointerDeviceType.Touch,
		_ => Sui.PointerDeviceType.Mouse,
	};

	private static Sui.PointerButtons ToPointerButton(MouseButton button) => button switch
	{
		MouseButton.Left => Sui.PointerButtons.Left,
		MouseButton.Right => Sui.PointerButtons.Right,
		MouseButton.Middle => Sui.PointerButtons.Middle,
		MouseButton.Button4 => Sui.PointerButtons.XButton1,
		MouseButton.Button5 => Sui.PointerButtons.XButton2,
		_ => Sui.PointerButtons.None,
	};

	// SUIsei's PointerUpdateKind models only the three primary buttons, so X-buttons are tracked in the button mask
	// but never raised as a routed press/release edge.
	private static Sui.PointerUpdateKind PressKind(MouseButton button) => button switch
	{
		MouseButton.Left => Sui.PointerUpdateKind.LeftButtonPressed,
		MouseButton.Right => Sui.PointerUpdateKind.RightButtonPressed,
		MouseButton.Middle => Sui.PointerUpdateKind.MiddleButtonPressed,
		_ => Sui.PointerUpdateKind.None,
	};

	private static Sui.PointerUpdateKind ReleaseKind(MouseButton button) => button switch
	{
		MouseButton.Left => Sui.PointerUpdateKind.LeftButtonReleased,
		MouseButton.Right => Sui.PointerUpdateKind.RightButtonReleased,
		MouseButton.Middle => Sui.PointerUpdateKind.MiddleButtonReleased,
		_ => Sui.PointerUpdateKind.None,
	};

	private static Sui.ModifierKeys ToModifier(Key key) => key switch
	{
		Key.LeftShift or Key.RightShift => Sui.ModifierKeys.Shift,
		Key.LeftControl or Key.RightControl => Sui.ModifierKeys.Control,
		Key.LeftAlt or Key.RightAlt => Sui.ModifierKeys.Alt,
		Key.LeftSuper or Key.RightSuper => Sui.ModifierKeys.Windows,
		_ => Sui.ModifierKeys.None,
	};

	// The neutral Key → SUIsei VirtualKey table this layer owns. VirtualKey is a WinUI-compatible subset, so keys
	// with no equivalent (extra function keys, keypad, most punctuation, the context-menu key) map to None and are
	// not forwarded. Letters/digits/F1-F12 map by contiguous offset; the rest are named explicitly.
	private static Sui.VirtualKey ToVirtualKey(Key key) => key switch
	{
		>= Key.A and <= Key.Z => Sui.VirtualKey.A + (key - Key.A),
		>= Key.Digit0 and <= Key.Digit9 => Sui.VirtualKey.Number0 + (key - Key.Digit0),
		>= Key.F1 and <= Key.F12 => Sui.VirtualKey.F1 + (key - Key.F1),
		Key.Space => Sui.VirtualKey.Space,
		Key.Enter or Key.KeypadEnter => Sui.VirtualKey.Enter,
		Key.Escape => Sui.VirtualKey.Escape,
		Key.Tab => Sui.VirtualKey.Tab,
		Key.Backspace => Sui.VirtualKey.Back,
		Key.Delete => Sui.VirtualKey.Delete,
		Key.Home => Sui.VirtualKey.Home,
		Key.End => Sui.VirtualKey.End,
		Key.PageUp => Sui.VirtualKey.PageUp,
		Key.PageDown => Sui.VirtualKey.PageDown,
		Key.Left => Sui.VirtualKey.Left,
		Key.Right => Sui.VirtualKey.Right,
		Key.Up => Sui.VirtualKey.Up,
		Key.Down => Sui.VirtualKey.Down,
		Key.LeftShift or Key.RightShift => Sui.VirtualKey.Shift,
		Key.LeftControl or Key.RightControl => Sui.VirtualKey.Control,
		Key.LeftAlt or Key.RightAlt => Sui.VirtualKey.Alt,
		_ => Sui.VirtualKey.None,
	};
}
