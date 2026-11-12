using System.Numerics;
using System.Text;
using Loom.Input;
using SUIsei;
using SUIsei.Controls;
using Sui = global::SUIsei.Input;

namespace Loom.UI.SUIsei;

/// <summary>
/// The input half of the SUIsei integration — the twin of the render-only <see cref="UIPainter"/>. An
/// <see cref="IInputLayer"/> that forwards a frame's neutral input into the <see cref="UIHost"/> it feeds, and keeps
/// from the lower (game) layers whatever the UI takes. It owns the neutral <see cref="Key"/> and
/// <see cref="GamepadButton"/> → <see cref="Sui.VirtualKey"/> tables — the second key table in the input stack (each
/// host's adapter owns the platform → neutral one). Push it above the game's layers and below any modal that must keep
/// seeing input while the UI is focused.
/// </summary>
/// <remarks>
/// <para>
/// The app drives <see cref="UIHost.Tick"/> itself each frame — that is what drains the queued events and refreshes the
/// capture report — so this layer queues events and decides what the UI takes from the host's <b>last</b> tick. A press
/// is decided on the spot, though: where it comes down on UI content (<see cref="UIHost.HitsContent"/>), the UI takes it
/// that same frame, so a click on a control never also reaches the game.
/// </para>
/// <para>
/// Touch is decided finger by finger. A finger that comes down on UI content belongs to the UI until it lifts, pressed,
/// dragged and released there as a touch pointer, and the game sees none of its touch events; a finger that comes down
/// anywhere else is the game's for its life, whatever it is dragged over, so one thumb can hold a button while the other
/// steers.
/// </para>
/// <para>
/// A controller goes to the UI while <see cref="PadNavigation"/> is set (a menu is open): see there.
/// </para>
/// </remarks>
public sealed class SUIseiInputLayer : IInputLayer
{
	private readonly UIHost _host;

	// Running pointer/modifier state, persisted across frames: neutral button/wheel/key events carry no position or
	// modifier set of their own, so a press or scroll reuses the latest move's position and the live modifier mask.
	private Vector2 _pointerPosition;
	private Sui.PointerButtons _buttons;
	private Sui.ModifierKeys _modifiers;
	// The pointer the latest move came from, which a press, release or scroll belongs to.
	private uint _pointerId;
	private Sui.PointerDeviceType _device = Sui.PointerDeviceType.Mouse;
	// Whether the pointer's press came down on UI content, until it is released.
	private bool _pressOnUI;

	// A pad through the UI (PadNavigation): the direction held, from the d-pad or the left stick, and when it next repeats
	private const float StickOn = 0.55f, StickOff = 0.35f;
	private const long RepeatFirstMs = 400, RepeatMs = 90;
	private Sui.VirtualKey _dpadHeld, _stickHeld;
	private Vector2 _stick;
	private long _repeatAt;
	// The touch contacts that came down on the UI and are its until they lift (TouchToUI)
	private readonly HashSet<int> _uiTouches = [];

	/// <summary>Creates a layer that feeds <paramref name="host"/>.</summary>
	public SUIseiInputLayer(UIHost host)
	{
		ArgumentNullException.ThrowIfNull(host);
		_host = host;
	}

	/// <summary>
	/// Whether the UI has the pad: while true, any pad's face buttons go to the UI as <see cref="Sui.VirtualKey.GamepadA"/>
	/// and the rest (Start and Back as GamepadMenu and GamepadView), and its d-pad and left stick, and the keyboard's arrows,
	/// as the d-pad's keys (moving focus by position, into the page when nothing is focused yet), a held direction repeating
	/// after 0.4 s every 0.09 s; and the pad is blocked from the layers below, so a press isn't also the game's.
	/// The app sets it while a menu or page that a pad can work is open, and clears it as play resumes.
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
		var pointerOnUIThisFrame = _pressOnUI;

		foreach (ref readonly var e in ctx.Incoming)
		{
			if (PadNavigation && e.Device == InputDevice.Gamepad)
			{
				PadToUI(in e);
				continue;
			}
			switch (e.Kind)
			{
				case InputEventKind.PointerMove:
					_pointerPosition = new Vector2(e.X, e.Y);
					_pointerId = (uint)e.PointerId;
					_device = ToDevice(e.PointerType);
					_host.QueuePointer(Sui.PointerInput.Move(_pointerPosition, _buttons, _pointerId, _device));
					break;

				case InputEventKind.MouseButtonDown:
					{
						if (_buttons == Sui.PointerButtons.None)
							_pressOnUI = _host.HitsContent(_pointerPosition);
						pointerOnUIThisFrame |= _pressOnUI;
						_buttons |= ToPointerButton(e.Button);
						var kind = PressKind(e.Button);
						if (kind != Sui.PointerUpdateKind.None)
							_host.QueuePointer(Sui.PointerInput.Press(_pointerPosition, kind, _buttons, _pointerId, _device));
						break;
					}

				case InputEventKind.MouseButtonUp:
					{
						_buttons &= ~ToPointerButton(e.Button);
						var kind = ReleaseKind(e.Button);
						if (kind != Sui.PointerUpdateKind.None)
							_host.QueuePointer(Sui.PointerInput.Release(_pointerPosition, kind, _buttons, _pointerId, _device));
						if (_buttons == Sui.PointerButtons.None)
							_pressOnUI = false;
						break;
					}

				case InputEventKind.Wheel:
					_host.QueuePointer(Sui.PointerInput.Scroll(_pointerPosition, new Vector2(e.X, e.Y), _buttons, _pointerId, _device));
					break;

				case InputEventKind.Touch:
					if (TouchToUI(in e))
						ctx.Consume(in e);
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

		// The mouse (its moves, buttons and wheel) and a pen's moves: where a press came down on UI content, the UI has them
		// from that very frame; otherwise the host's last-tick report decides. Never a touch, which is the UI's or not
		// contact by contact (TouchToUI), so a finger on the UI doesn't take every other finger from the game.
		if (pointerOnUIThisFrame || _host.WantsPointer)
		{
			ctx.Block(InputDevice.Mouse);
			foreach (ref readonly var e in ctx.Incoming)
				if (e.Kind == InputEventKind.PointerMove && e.PointerType != PointerDeviceType.Touch)
					ctx.Consume(in e);   // a pen's moves, which aren't the mouse's
		}
		if (_host.WantsKeyboard)
			ctx.Block(InputDevice.Keyboard);
		if (PadNavigation)
		{
			RepeatHeld();
			ctx.Block(InputDevice.Gamepad);
		}

		// A focused text box takes typed text, so a host whose keyboard is on the screen shows it.
		if (_host.Focus.FocusedElement is TextBox)
			ctx.RequestTextInput();
	}

	// A pad's event as the UI's: face buttons as the gamepad's keys, the d-pad as its direction keys, the left stick past
	// half way as the d-pad (back under a third, let go)
	private void PadToUI(in InputEvent e)
	{
		switch (e.Kind)
		{
			case InputEventKind.GamepadButtonDown or InputEventKind.GamepadButtonUp:
				{
					bool down = e.Kind == InputEventKind.GamepadButtonDown;
					var key = ToGamepadKey((GamepadButton)e.Code);
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

	// A touch as the UI's, if it is the UI's: a contact that comes down on UI content (UIHost.HitsContent) is the UI's until
	// it lifts, pressed, dragged and released there as a touch pointer, and true is returned so it is kept from the layers
	// below; any other contact is theirs for its life, whatever it is dragged over, so a finger on a button and another on
	// the game work at once. Ids are kept apart from the mouse's (0) by the top bit.
	private bool TouchToUI(in InputEvent e)
	{
		var id = (uint)e.PointerId | 0x8000_0000u;
		var at = new Vector2(e.X, e.Y);
		switch (e.Phase)
		{
			case TouchPhase.Began:
				if (!_host.HitsContent(at))
					return false;
				_uiTouches.Add(e.PointerId);
				_host.QueuePointer(Sui.PointerInput.Move(at, Sui.PointerButtons.None, id, Sui.PointerDeviceType.Touch));
				_host.QueuePointer(Sui.PointerInput.Press(at, Sui.PointerUpdateKind.LeftButtonPressed, Sui.PointerButtons.Left, id, Sui.PointerDeviceType.Touch));
				return true;

			case TouchPhase.Moved or TouchPhase.Stationary:
				if (!_uiTouches.Contains(e.PointerId))
					return false;
				_host.QueuePointer(Sui.PointerInput.Move(at, Sui.PointerButtons.Left, id, Sui.PointerDeviceType.Touch));
				return true;

			default:   // Ended, Canceled
				if (!_uiTouches.Remove(e.PointerId))
					return false;
				_host.QueuePointer(Sui.PointerInput.Release(at, Sui.PointerUpdateKind.LeftButtonReleased, Sui.PointerButtons.None, id, Sui.PointerDeviceType.Touch));
				return true;
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

	// The neutral pad button → SUIsei's gamepad keys, as WinUI numbers them. Positional: South is A whatever it is
	// labelled (a PlayStation pad's cross). The bumpers and stick buttons have no SUIsei key and stay the game's.
	private static Sui.VirtualKey ToGamepadKey(GamepadButton button) => button switch
	{
		GamepadButton.South => Sui.VirtualKey.GamepadA,
		GamepadButton.East => Sui.VirtualKey.GamepadB,
		GamepadButton.West => Sui.VirtualKey.GamepadX,
		GamepadButton.North => Sui.VirtualKey.GamepadY,
		GamepadButton.DPadUp => Sui.VirtualKey.GamepadDPadUp,
		GamepadButton.DPadDown => Sui.VirtualKey.GamepadDPadDown,
		GamepadButton.DPadLeft => Sui.VirtualKey.GamepadDPadLeft,
		GamepadButton.DPadRight => Sui.VirtualKey.GamepadDPadRight,
		GamepadButton.Start => Sui.VirtualKey.GamepadMenu,
		GamepadButton.Back => Sui.VirtualKey.GamepadView,
		_ => Sui.VirtualKey.None,
	};
}
