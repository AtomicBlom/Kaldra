using Android.Views;

using Loom.Core.Hosting;
using Loom.Input;

// Android.Views has an InputEvent of its own, the base of MotionEvent.
using InputEvent = Loom.Input.InputEvent;

namespace Loom.Hosting.Android;

/// <summary>
/// Turns Android's touch, mouse, pen and keyboard input into the app's neutral input.
/// </summary>
/// <remarks>
/// <para>
/// Every finger and pen contact becomes <see cref="InputEventKind.Touch"/> events (began, moved, ended, canceled) under its
/// pointer id, for an app that reads multi-touch. The primary contact — the first finger down, or a pen — is also sent the
/// way a pointer is on the desktop and in the browser: a <see cref="InputEventKind.PointerMove"/> typed as touch or pen,
/// with the left button going down and up. That is what a UI layer such as SUIsei's routes as a press, a drag and a
/// click, so a tap on a button clicks it with nothing touch-specific in the UI. A pen's barrel buttons are the right and
/// middle buttons.
/// </para>
/// <para>
/// A mouse is a pointer, not a contact: it moves the pointer whether or not a button is down, and each of its buttons is
/// its own, so a right-click is a right-click. Its wheel scrolls.
/// </para>
/// <para>
/// A keyboard's keys are sent as keys and what they type as text, the way the desktop sends them; the on-screen
/// keyboard's text and edits come in through <see cref="QueueText"/> and <see cref="PressKey"/>. Runs on the UI thread,
/// the thread the activity also runs the app's frames on, so it queues straight into the app.
/// </para>
/// </remarks>
internal sealed class AndroidInputPump(IHostedApplication app)
{
	// The contact acting as the pointer, or -1 while no contact is down.
	private int _primaryId = -1;

	// What is held, so focus loss can release it: keys by neutral value, mouse buttons as a mask.
	private readonly bool[] _keysHeld = new bool[256];
	private uint _mouseHeld;

	// A mouse press injected without a button state (adb's, a remote's) stands for the left button, released with it.
	private bool _mouseContactIsLeft;

	/// <summary>
	/// Whether the app is taking frames, and so draining its input queue each one. While it is not (paused but still
	/// visible, as in split screen), moves and new presses are dropped rather than left to fill the queue; a lift or a
	/// cancel still ends a press already held.
	/// </summary>
	public bool Accepting { get; set; }

	/// <summary>Queue the input in a touch, pen or mouse-button event. Returns whether the event was taken.</summary>
	public bool OnTouch(MotionEvent e)
	{
		var action = e.ActionMasked;
		if (!Accepting && action is not (MotionEventActions.Up or MotionEventActions.PointerUp or MotionEventActions.Cancel
			or MotionEventActions.ButtonRelease))
			return true; // taken, and dropped: nothing drains the queue until frames run again

		if (action is MotionEventActions.ButtonPress or MotionEventActions.ButtonRelease)
			return OnButton(e);
		if (e.GetToolType(e.ActionIndex) == MotionEventToolType.Mouse)
			return OnMouse(e);

		var timestamp = e.EventTime;
		switch (action)
		{
			case MotionEventActions.Down:
			case MotionEventActions.PointerDown:
				{
					var index = e.ActionIndex;
					var id = e.GetPointerId(index);
					var x = e.GetX(index);
					var y = e.GetY(index);
					app.QueueInput(InputEvent.Touch(TouchPhase.Began, id, x, y, timestamp));
					if (_primaryId < 0)
					{
						_primaryId = id;
						app.QueueInput(InputEvent.PointerMove(x, y, id, PointerTypeOf(e, index), timestamp));
						SetMouseButton(MouseButton.Left, down: true, timestamp);
					}
					return true;
				}
			case MotionEventActions.Move:
				for (var index = 0; index < e.PointerCount; index++)
				{
					var id = e.GetPointerId(index);
					var x = e.GetX(index);
					var y = e.GetY(index);
					app.QueueInput(InputEvent.Touch(TouchPhase.Moved, id, x, y, timestamp));
					if (id == _primaryId)
						app.QueueInput(InputEvent.PointerMove(x, y, id, PointerTypeOf(e, index), timestamp));
				}
				return true;
			case MotionEventActions.Up:
			case MotionEventActions.PointerUp:
				{
					var index = e.ActionIndex;
					var id = e.GetPointerId(index);
					var x = e.GetX(index);
					var y = e.GetY(index);
					app.QueueInput(InputEvent.Touch(TouchPhase.Ended, id, x, y, timestamp));
					if (id == _primaryId)
					{
						app.QueueInput(InputEvent.PointerMove(x, y, id, PointerTypeOf(e, index), timestamp));
						SetMouseButton(MouseButton.Left, down: false, timestamp);
						_primaryId = -1;
					}
					return true;
				}
			case MotionEventActions.Cancel:
				// The system took every contact away (a gesture it recognised, the activity losing the window).
				for (var index = 0; index < e.PointerCount; index++)
					app.QueueInput(InputEvent.Touch(TouchPhase.Canceled, e.GetPointerId(index), e.GetX(index), e.GetY(index), timestamp));
				ReleasePrimary(timestamp);
				return true;
			default:
				return false;
		}
	}

	/// <summary>
	/// Queue a hovering mouse or pen's move, a mouse wheel's scroll, or a mouse or pen button. Returns whether the event
	/// was taken.
	/// </summary>
	public bool OnGenericMotion(MotionEvent e)
	{
		if (!Accepting && e.ActionMasked != MotionEventActions.ButtonRelease)
			return false;

		var timestamp = e.EventTime;
		switch (e.ActionMasked)
		{
			case MotionEventActions.HoverMove:
			case MotionEventActions.HoverEnter:
				if (_primaryId >= 0)
					return false; // a contact is down and already drives the pointer
				app.QueueInput(InputEvent.PointerMove(e.GetX(0), e.GetY(0), e.GetPointerId(0), PointerTypeOf(e, 0), timestamp));
				return true;
			case MotionEventActions.Scroll:
				app.QueueInput(InputEvent.Wheel(e.GetAxisValue(Axis.Hscroll), e.GetAxisValue(Axis.Vscroll), timestamp));
				return true;
			case MotionEventActions.ButtonPress:
			case MotionEventActions.ButtonRelease:
				return OnButton(e);
			default:
				return false;
		}
	}

	/// <summary>
	/// Queue a keyboard key's press or release, and the character it types. Returns whether the event was taken; one with
	/// neither a key the app knows nor a character (a volume key) is left to the system.
	/// </summary>
	public bool OnKey(KeyEvent e)
	{
		var timestamp = e.EventTime;
		var key = AndroidKeyMap.ToKey(e.KeyCode);
		var down = e.Action == KeyEventActions.Down;
		var character = down ? e.UnicodeChar : 0;
		var types = character >= 0x20 && character != 0x7F && (character & unchecked((int)KeyCharacterMap.CombiningAccent)) == 0;
		if (key == Key.Unknown && !types)
			return false;
		if (!Accepting && down)
			return true; // taken, and dropped

		if (key != Key.Unknown)
		{
			if (down && e.RepeatCount == 0 && !_keysHeld[(int)key])
			{
				_keysHeld[(int)key] = true;
				app.QueueInput(InputEvent.KeyDown(key, timestamp));
			}
			else if (e.Action == KeyEventActions.Up && _keysHeld[(int)key])
			{
				_keysHeld[(int)key] = false;
				app.QueueInput(InputEvent.KeyUp(key, timestamp));
			}
		}
		// A held key repeats its character, as a desktop keyboard does.
		if (types)
			app.QueueInput(InputEvent.Text((uint)character, timestamp));
		return true;
	}

	/// <summary>Queue typed text, one text event per character.</summary>
	public void QueueText(ReadOnlySpan<char> text, long timestamp)
	{
		if (!Accepting)
			return;
		foreach (var rune in text.EnumerateRunes())
			app.QueueInput(InputEvent.Text((uint)rune.Value, timestamp));
	}

	/// <summary>Queue a key pressed and released at once: an on-screen keyboard's Backspace, Delete or Enter.</summary>
	public void PressKey(Key key, long timestamp)
	{
		if (!Accepting || _keysHeld[(int)key])
			return;
		app.QueueInput(InputEvent.KeyDown(key, timestamp));
		app.QueueInput(InputEvent.KeyUp(key, timestamp));
	}

	/// <summary>
	/// End a press a contact holds without it counting as a click: for a contact the system canceled, or focus lost
	/// mid-press, whose up never comes. The pointer moves off the surface first, so the pressed element sees it leave
	/// and the release lands on nothing.
	/// </summary>
	public void ReleasePrimary(long timestamp = 0)
	{
		if (_primaryId < 0)
			return;
		app.QueueInput(InputEvent.PointerMove(-1f, -1f, _primaryId, PointerDeviceType.Touch, timestamp));
		SetMouseButton(MouseButton.Left, down: false, timestamp);
		_primaryId = -1;
	}

	/// <summary>
	/// Release everything held — the primary contact, keys and mouse buttons — for the window losing focus, after which
	/// their releases never come.
	/// </summary>
	public void ReleaseAll(long timestamp)
	{
		ReleasePrimary(timestamp);
		for (var i = 0; i < _keysHeld.Length; i++)
		{
			if (!_keysHeld[i])
				continue;
			_keysHeld[i] = false;
			app.QueueInput(InputEvent.KeyUp((Key)i, timestamp));
		}
		while (_mouseHeld != 0)
			SetMouseButton((MouseButton)System.Numerics.BitOperations.TrailingZeroCount(_mouseHeld), down: false, timestamp);
		_mouseContactIsLeft = false;
	}

	// A mouse's contact events: it moves the pointer, and its buttons arrive on their own (OnButton), unless the press was
	// injected with no button state at all, which stands for the left button.
	private bool OnMouse(MotionEvent e)
	{
		var timestamp = e.EventTime;
		var id = e.GetPointerId(e.ActionIndex);
		app.QueueInput(InputEvent.PointerMove(e.GetX(e.ActionIndex), e.GetY(e.ActionIndex), id, PointerDeviceType.Mouse, timestamp));
		switch (e.ActionMasked)
		{
			case MotionEventActions.Down when e.ButtonState == 0:
				_mouseContactIsLeft = true;
				SetMouseButton(MouseButton.Left, down: true, timestamp);
				break;
			case MotionEventActions.Up or MotionEventActions.Cancel when _mouseContactIsLeft:
				_mouseContactIsLeft = false;
				SetMouseButton(MouseButton.Left, down: false, timestamp);
				break;
		}
		return true;
	}

	// A mouse button, or a pen's barrel button, going down or up.
	private bool OnButton(MotionEvent e)
	{
		if (ToMouseButton(e.ActionButton) is not { } button)
			return false;
		var timestamp = e.EventTime;
		app.QueueInput(InputEvent.PointerMove(e.GetX(0), e.GetY(0), e.GetPointerId(0), PointerTypeOf(e, 0), timestamp));
		SetMouseButton(button, e.ActionMasked == MotionEventActions.ButtonPress, timestamp);
		return true;
	}

	private void SetMouseButton(MouseButton button, bool down, long timestamp)
	{
		var bit = 1u << (int)button;
		if (down == ((_mouseHeld & bit) != 0))
			return;
		_mouseHeld = down ? _mouseHeld | bit : _mouseHeld & ~bit;
		app.QueueInput(down ? InputEvent.MouseButtonDown(button, timestamp) : InputEvent.MouseButtonUp(button, timestamp));
	}

	private static MouseButton? ToMouseButton(MotionEventButtonState button) => button switch
	{
		MotionEventButtonState.Primary => MouseButton.Left,
		MotionEventButtonState.Secondary => MouseButton.Right,
		MotionEventButtonState.Tertiary => MouseButton.Middle,
		MotionEventButtonState.Back => MouseButton.Button4,
		MotionEventButtonState.Forward => MouseButton.Button5,
		MotionEventButtonState.StylusPrimary => MouseButton.Right,
		MotionEventButtonState.StylusSecondary => MouseButton.Middle,
		_ => null,
	};

	private static PointerDeviceType PointerTypeOf(MotionEvent e, int index) => e.GetToolType(index) switch
	{
		MotionEventToolType.Finger => PointerDeviceType.Touch,
		MotionEventToolType.Stylus or MotionEventToolType.Eraser => PointerDeviceType.Pen,
		MotionEventToolType.Mouse => PointerDeviceType.Mouse,
		_ => PointerDeviceType.Unknown,
	};
}
