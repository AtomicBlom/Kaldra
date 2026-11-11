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

	/// <summary>Queue the input in a touch, pen or mouse-button event. Returns whether the event was taken.</summary>
	public bool OnTouch(MotionEvent e)
	{
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

	/// <summary>Release the pointer's button if a contact holds it: for focus lost mid-press, whose up never comes.</summary>
	public void ReleasePrimary(long timestamp = 0)
	{
		if (_primaryId < 0)
			return;
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
