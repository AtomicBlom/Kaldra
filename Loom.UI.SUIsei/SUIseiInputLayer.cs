using System.Numerics;
using System.Text;
using Loom.Input;
using SUIsei;
using SUIsei.Controls;
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

	/// <summary>Creates a layer that feeds <paramref name="host"/>.</summary>
	public SUIseiInputLayer(UIHost host)
	{
		ArgumentNullException.ThrowIfNull(host);
		_host = host;
	}

	/// <inheritdoc/>
	public void Process(ref InputContext ctx)
	{
		foreach (ref readonly var e in ctx.Incoming)
		{
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

		// A focused text box takes typed text, so a host whose keyboard is on the screen shows it.
		if (_host.Focus.FocusedElement is TextBox)
			ctx.RequestTextInput();
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
}
