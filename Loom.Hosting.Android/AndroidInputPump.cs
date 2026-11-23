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
/// Every finger is its own contact: <see cref="InputEventKind.Touch"/> events (began, moved, ended, canceled) under its
/// pointer id, and nothing else, as on the desktop and in the browser. A UI layer such as Vysara's routes the fingers that
/// land on it as touch pointers; the rest are the game's. A pen is a pointer, as a mouse is: a
/// <see cref="InputEventKind.PointerMove"/> typed as pen, with the left button going down and up as its tip touches and
/// lifts, and its barrel buttons as the right and middle buttons.
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
	// The pen touching the screen, which drives the pointer, or -1 while none is.
	private int _primaryId = -1;

	// The fingers down and where each last was, so focus loss can cancel them.
	private readonly Dictionary<int, (float X, float Y)> _fingers = [];

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
					if (!IsPen(e, index))
					{
						_fingers[id] = (x, y);
						app.QueueInput(InputEvent.Touch(TouchPhase.Began, id, x, y, timestamp));
					}
					else if (_primaryId < 0)
					{
						_primaryId = id;
						app.QueueInput(InputEvent.PointerMove(x, y, id, PointerDeviceType.Pen, timestamp));
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
					if (id == _primaryId)
					{
						app.QueueInput(InputEvent.PointerMove(x, y, id, PointerDeviceType.Pen, timestamp));
					}
					else if (_fingers.ContainsKey(id))
					{
						_fingers[id] = (x, y);
						app.QueueInput(InputEvent.Touch(TouchPhase.Moved, id, x, y, timestamp));
					}
				}
				return true;
			case MotionEventActions.Up:
			case MotionEventActions.PointerUp:
				{
					var index = e.ActionIndex;
					var id = e.GetPointerId(index);
					var x = e.GetX(index);
					var y = e.GetY(index);
					if (id == _primaryId)
					{
						app.QueueInput(InputEvent.PointerMove(x, y, id, PointerDeviceType.Pen, timestamp));
						SetMouseButton(MouseButton.Left, down: false, timestamp);
						_primaryId = -1;
					}
					else if (_fingers.Remove(id))
					{
						app.QueueInput(InputEvent.Touch(TouchPhase.Ended, id, x, y, timestamp));
					}
					return true;
				}
			case MotionEventActions.Cancel:
				// The system took every contact away (a gesture it recognised, the activity losing the window).
				ReleaseContacts(timestamp);
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
					return false; // the pen is down and already drives the pointer
				app.QueueInput(InputEvent.PointerMove(e.GetX(0), e.GetY(0), e.GetPointerId(0), PointerTypeOf(e, 0), timestamp));
				return true;
			case MotionEventActions.Scroll:
				// The wheel scrolls what is under it, so the pointer moves there first, unless the pen holds it.
				if (_primaryId < 0)
					app.QueueInput(InputEvent.PointerMove(e.GetX(0), e.GetY(0), e.GetPointerId(0), PointerTypeOf(e, 0), timestamp));
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
	/// End every contact without it counting as a tap or a click: for contacts the system canceled, or focus lost
	/// mid-press, whose ups never come. Each finger is canceled; a pen's pointer moves off the surface first, so the
	/// pressed element sees it leave and the release lands on nothing.
	/// </summary>
	public void ReleaseContacts(long timestamp = 0)
	{
		foreach (var (id, at) in _fingers)
			app.QueueInput(InputEvent.Touch(TouchPhase.Canceled, id, at.X, at.Y, timestamp));
		_fingers.Clear();
		if (_primaryId < 0)
			return;
		app.QueueInput(InputEvent.PointerMove(-1f, -1f, _primaryId, PointerDeviceType.Pen, timestamp));
		SetMouseButton(MouseButton.Left, down: false, timestamp);
		_primaryId = -1;
	}

	/// <summary>
	/// Release everything held — fingers, the pen, keys and mouse buttons — for the window losing focus, after which
	/// their releases never come.
	/// </summary>
	public void ReleaseAll(long timestamp)
	{
		ReleaseContacts(timestamp);
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

	private static bool IsPen(MotionEvent e, int index)
		=> e.GetToolType(index) is MotionEventToolType.Stylus or MotionEventToolType.Eraser;

	private static PointerDeviceType PointerTypeOf(MotionEvent e, int index) => e.GetToolType(index) switch
	{
		MotionEventToolType.Finger => PointerDeviceType.Touch,
		MotionEventToolType.Stylus or MotionEventToolType.Eraser => PointerDeviceType.Pen,
		MotionEventToolType.Mouse => PointerDeviceType.Mouse,
		_ => PointerDeviceType.Unknown,
	};
}
