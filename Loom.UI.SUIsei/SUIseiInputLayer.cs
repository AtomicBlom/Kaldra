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
/// Touch is decided finger by finger. A finger that comes down on UI content belongs to the UI until it lifts, and the
/// game sees none of its touch events; a finger that comes down anywhere else is the game's, so one thumb can hold a
/// button while the other steers.
/// </para>
/// <para>
/// A controller goes to the UI while <see cref="TakesGamepad"/> is set (a menu is open), or while an element holds focus
/// it got from the keyboard, a controller or code — not from a tap, which would take the pad from the game whenever a
/// button had last been touched. Then the A, B, X and Y buttons, Start and Back reach SUIsei as its gamepad keys, the
/// d-pad and the left stick move focus between controls, and the game sees no controller input.
/// </para>
/// </remarks>
public sealed class SUIseiInputLayer : IInputLayer
{
	private const int PadCount = 4;
	// How far the left stick goes before it moves focus, and how far back before it can again.
	private const float StickPress = 0.5f;
	private const float StickRelease = 0.3f;

	private readonly UIHost _host;

	// Running pointer/modifier state, persisted across frames: neutral button/wheel/key events carry no position or
	// modifier set of their own, so a press or scroll reuses the latest move's position and the live modifier mask.
	private Vector2 _pointerPosition;
	private Sui.PointerButtons _buttons;
	private Sui.ModifierKeys _modifiers;
	// The pointer the latest move came from, which a press, release or scroll belongs to: a press by touch is a touch
	// press, so SUIsei can treat a drag from it as a pan.
	private uint _pointerId;
	private Sui.PointerDeviceType _device = Sui.PointerDeviceType.Mouse;
	// Whether the pointer's press came down on UI content, until it is released.
	private bool _pressOnUI;

	// The fingers that came down on UI content and have not lifted.
	private readonly HashSet<int> _uiTouches = [];

	// Per pad: the left stick's position, and the direction it is held in as a d-pad key (None when centred).
	private readonly Vector2[] _stick = new Vector2[PadCount];
	private readonly Sui.VirtualKey[] _stickKey = new Sui.VirtualKey[PadCount];

	/// <summary>Creates a layer that feeds <paramref name="host"/>.</summary>
	public SUIseiInputLayer(UIHost host)
	{
		ArgumentNullException.ThrowIfNull(host);
		_host = host;
	}

	/// <summary>
	/// Whether the UI has the controllers, whatever holds focus: set it while a menu or other page a player drives with a
	/// pad is open, and clear it as play resumes. While set, every pad button reaches SUIsei (the d-pad moves focus into
	/// the page when nothing is focused yet) and the game sees no controller input.
	/// </summary>
	public bool TakesGamepad { get; set; }

	/// <inheritdoc/>
	public void Process(ref InputContext ctx)
	{
		// Decided from the last tick, before this frame's events: a pad press that focuses a control this frame takes the
		// pad from the next.
		var focus = _host.Focus;
		var padToUI = TakesGamepad || focus.FocusedElement is not null && focus.FocusState is not (FocusState.Pointer or FocusState.Unfocused);
		var pointerOnUIThisFrame = _pressOnUI;

		foreach (ref readonly var e in ctx.Incoming)
		{
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
					// A finger is the UI's from the moment it lands on UI content until it lifts; the game never sees it.
					if (e.Phase == TouchPhase.Began && _host.HitsContent(e.Position))
						_uiTouches.Add(e.PointerId);
					if (_uiTouches.Contains(e.PointerId))
					{
						ctx.Consume(in e);
						if (e.Phase is TouchPhase.Ended or TouchPhase.Canceled)
							_uiTouches.Remove(e.PointerId);
					}
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

				case InputEventKind.GamepadButtonDown or InputEventKind.GamepadButtonUp when padToUI:
					{
						var vk = ToGamepadKey(e.GamepadButton);
						if (vk != Sui.VirtualKey.None)
							_host.QueueKey(new Sui.KeyInput(vk, Down: e.Kind == InputEventKind.GamepadButtonDown, Repeat: false, _modifiers, 0));
						break;
					}

				case InputEventKind.GamepadAxis when (uint)e.PadIndex < PadCount
					&& e.GamepadAxis is GamepadAxis.LeftX or GamepadAxis.LeftY:
					MoveStick(e.PadIndex, e.GamepadAxis, e.Value, padToUI);
					break;
			}
		}

		// The pointer: where its press came down on UI content, the UI has it from that very frame; otherwise the host's
		// last-tick report decides. A touch-driven pointer gives the UI only its own moves and buttons, so a game's other
		// fingers keep their touches; a mouse or pen gives it every pointer event.
		if (pointerOnUIThisFrame || _host.WantsPointer)
		{
			if (_device == Sui.PointerDeviceType.Touch)
			{
				ctx.Block(InputDevice.Mouse);
				foreach (ref readonly var e in ctx.Incoming)
					if (e is { Kind: InputEventKind.PointerMove, PointerType: PointerDeviceType.Touch } && (uint)e.PointerId == _pointerId)
						ctx.Consume(in e);
			}
			else
			{
				ctx.Block(InputDevice.Pointer | InputDevice.Mouse);
			}
		}
		if (_host.WantsKeyboard)
			ctx.Block(InputDevice.Keyboard);
		if (padToUI)
			ctx.Block(InputDevice.Gamepad);

		// A focused text box takes typed text, so a host whose keyboard is on the screen shows it.
		if (focus.FocusedElement is TextBox)
			ctx.RequestTextInput();
	}

	// The left stick as a d-pad: pushed past StickPress it presses the direction it points most along, and releases it
	// once back inside StickRelease or turned to another direction. Tracked even while the game has the pad, so a stick
	// already held as the UI takes it does not move focus until it is pushed again.
	private void MoveStick(int pad, GamepadAxis axis, float value, bool padToUI)
	{
		var stick = _stick[pad];
		stick = axis == GamepadAxis.LeftX ? stick with { X = value } : stick with { Y = value };
		_stick[pad] = stick;

		var held = _stickKey[pad];
		var reach = held == Sui.VirtualKey.None ? StickPress : StickRelease;
		var direction = MathF.Max(MathF.Abs(stick.X), MathF.Abs(stick.Y)) < reach
			? Sui.VirtualKey.None
			: MathF.Abs(stick.X) >= MathF.Abs(stick.Y)
				? (stick.X < 0f ? Sui.VirtualKey.GamepadDPadLeft : Sui.VirtualKey.GamepadDPadRight)
				: (stick.Y < 0f ? Sui.VirtualKey.GamepadDPadUp : Sui.VirtualKey.GamepadDPadDown); // y is down, as on every host
		if (direction == held)
			return;
		_stickKey[pad] = direction;
		if (!padToUI)
			return;
		if (held != Sui.VirtualKey.None)
			_host.QueueKey(new Sui.KeyInput(held, Down: false, Repeat: false, _modifiers, 0));
		if (direction != Sui.VirtualKey.None)
			_host.QueueKey(new Sui.KeyInput(direction, Down: true, Repeat: false, _modifiers, 0));
	}

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
