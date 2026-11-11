using Android.Views;

using Loom.Core.Hosting;
using Loom.Input;

// Android.Views has an InputEvent of its own, the base of MotionEvent.
using InputEvent = Loom.Input.InputEvent;

namespace Loom.Hosting.Android;

/// <summary>
/// Turns Android <see cref="MotionEvent"/>s into the app's neutral input. Every contact becomes
/// <see cref="InputEventKind.Touch"/> events (began, moved, ended, canceled) under its pointer id, for an app that reads
/// multi-touch. The primary contact — the first finger down, a pen, or a mouse — is also sent the way a pointer is on the
/// desktop and in the browser: a <see cref="InputEventKind.PointerMove"/> typed as touch, pen or mouse, with the left
/// button going down and up. That is what a UI layer such as SUIsei's routes as a press, a drag and a click, so a tap on
/// a button clicks it with nothing touch-specific in the UI. A hovering mouse or pen moves the pointer, and a mouse
/// wheel scrolls.
/// </summary>
/// <remarks>Runs on the UI thread, the thread the activity also runs the app's frames on, so it queues straight into the app.</remarks>
internal sealed class AndroidInputPump(IHostedApplication app)
{
	// The contact acting as the pointer, or -1 while no contact is down.
	private int _primaryId = -1;

	/// <summary>
	/// Whether the app is taking frames, and so draining its input queue each one. While it is not (paused but still
	/// visible, as in split screen), moves and new presses are dropped rather than left to fill the queue; a lift or a
	/// cancel still ends a press already held.
	/// </summary>
	public bool Accepting { get; set; }

	/// <summary>Queue the input in a touch, pen or mouse-button event. Returns whether the event was taken.</summary>
	public bool OnTouch(MotionEvent e)
	{
		if (!Accepting && e.ActionMasked is not (MotionEventActions.Up or MotionEventActions.PointerUp or MotionEventActions.Cancel))
			return true; // taken, and dropped: nothing drains the queue until frames run again

		var timestamp = e.EventTime;
		switch (e.ActionMasked)
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
						app.QueueInput(InputEvent.MouseButtonDown(MouseButton.Left, timestamp));
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
						app.QueueInput(InputEvent.MouseButtonUp(MouseButton.Left, timestamp));
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

	/// <summary>Queue a hovering mouse or pen's move, or a mouse wheel's scroll. Returns whether the event was taken.</summary>
	public bool OnGenericMotion(MotionEvent e)
	{
		if (!Accepting)
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
			default:
				return false;
		}
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
		app.QueueInput(InputEvent.MouseButtonUp(MouseButton.Left, timestamp));
		_primaryId = -1;
	}

	private static PointerDeviceType PointerTypeOf(MotionEvent e, int index) => e.GetToolType(index) switch
	{
		MotionEventToolType.Finger => PointerDeviceType.Touch,
		MotionEventToolType.Stylus or MotionEventToolType.Eraser => PointerDeviceType.Pen,
		MotionEventToolType.Mouse => PointerDeviceType.Mouse,
		_ => PointerDeviceType.Unknown,
	};
}
