using ImGuiNET;
using BinaryVibrance.Kaldra.Input;
using Im = ImGuiNET.ImGui;

namespace BinaryVibrance.Kaldra.UI.ImGui;

/// <summary>
/// The input half of the Dear ImGui integration — the twin of the render-only <see cref="ImGuiPainter"/>. An
/// <see cref="IInputLayer"/> that feeds a frame's neutral input into the global ImGui context's IO, then blocks
/// lower (game) layers whenever ImGui reports it captured the mouse or keyboard. ImGui derives
/// <c>WantCaptureMouse</c>/<c>WantCaptureKeyboard</c> itself from the fed events and its hovered/active state, so —
/// unlike the Vysara glue — no submodule change is needed for the report. Push it above the game's layers.
/// </summary>
/// <remarks>
/// ImGui's capture flags reflect the state established by the <i>previous</i> frame's <c>NewFrame</c>, so the block
/// uses last frame's result — one frame of coarse latency, matching the Vysara glue. The very first click onto an
/// ImGui widget is therefore not yet captured and can "click through" to the game on that frame, after which the
/// block engages. The app owns the ImGui context lifecycle (<c>NewFrame</c>/<c>Render</c>); this layer only feeds
/// IO events and reads the flags off the same global context the <see cref="ImGuiPainter"/> renders from.
/// </remarks>
public sealed class ImGuiInputLayer : IInputLayer
{
	/// <inheritdoc/>
	public void Process(ref InputContext ctx)
	{
		var io = Im.GetIO();

		foreach (ref readonly var e in ctx.Incoming)
		{
			switch (e.Kind)
			{
				case InputEventKind.PointerMove:
					io.AddMousePosEvent(e.X, e.Y);
					break;

				// Kaldra's MouseButton values (Left=0, Right=1, Middle=2, Button4=3, Button5=4) already match ImGui's
				// button indices, so the raw cast needs no remap.
				case InputEventKind.MouseButtonDown:
					io.AddMouseButtonEvent((int)e.Button, true);
					break;
				case InputEventKind.MouseButtonUp:
					io.AddMouseButtonEvent((int)e.Button, false);
					break;

				case InputEventKind.Wheel:
					io.AddMouseWheelEvent(e.X, e.Y);
					break;

				case InputEventKind.KeyDown:
					{
						var key = ToImGuiKey(e.Key);
						if (key != ImGuiKey.None)
							io.AddKeyEvent(key, true);
						break;
					}
				case InputEventKind.KeyUp:
					{
						var key = ToImGuiKey(e.Key);
						if (key != ImGuiKey.None)
							io.AddKeyEvent(key, false);
						break;
					}

				case InputEventKind.Text:
					io.AddInputCharacter(e.CodePoint);
					break;
			}
		}

		if (io.WantCaptureMouse)
			ctx.Block(InputDevice.Mouse);
		if (io.WantCaptureKeyboard)
			ctx.Block(InputDevice.Keyboard);
	}

	// The neutral Key → ImGuiKey table. ImGui tracks modifier state from the physical L/R modifier key events
	// (it derives io.KeyCtrl/KeyShift/… internally), so feeding those keys is enough — no separate mod events.
	// Contiguous ranges map by offset; the rest are named. Unmapped keys map to None and are not forwarded.
	private static ImGuiKey ToImGuiKey(Key key) => key switch
	{
		>= Key.A and <= Key.Z => ImGuiKey.A + (key - Key.A),
		>= Key.Digit0 and <= Key.Digit9 => ImGuiKey._0 + (key - Key.Digit0),
		>= Key.F1 and <= Key.F12 => ImGuiKey.F1 + (key - Key.F1),
		>= Key.F13 and <= Key.F24 => ImGuiKey.F13 + (key - Key.F13),
		>= Key.Keypad0 and <= Key.Keypad9 => ImGuiKey.Keypad0 + (key - Key.Keypad0),
		Key.Space => ImGuiKey.Space,
		Key.Enter => ImGuiKey.Enter,
		Key.Escape => ImGuiKey.Escape,
		Key.Tab => ImGuiKey.Tab,
		Key.Backspace => ImGuiKey.Backspace,
		Key.Insert => ImGuiKey.Insert,
		Key.Delete => ImGuiKey.Delete,
		Key.Home => ImGuiKey.Home,
		Key.End => ImGuiKey.End,
		Key.PageUp => ImGuiKey.PageUp,
		Key.PageDown => ImGuiKey.PageDown,
		Key.Left => ImGuiKey.LeftArrow,
		Key.Right => ImGuiKey.RightArrow,
		Key.Up => ImGuiKey.UpArrow,
		Key.Down => ImGuiKey.DownArrow,
		Key.LeftShift => ImGuiKey.LeftShift,
		Key.RightShift => ImGuiKey.RightShift,
		Key.LeftControl => ImGuiKey.LeftCtrl,
		Key.RightControl => ImGuiKey.RightCtrl,
		Key.LeftAlt => ImGuiKey.LeftAlt,
		Key.RightAlt => ImGuiKey.RightAlt,
		Key.LeftSuper => ImGuiKey.LeftSuper,
		Key.RightSuper => ImGuiKey.RightSuper,
		Key.Menu => ImGuiKey.Menu,
		Key.CapsLock => ImGuiKey.CapsLock,
		Key.NumLock => ImGuiKey.NumLock,
		Key.ScrollLock => ImGuiKey.ScrollLock,
		Key.PrintScreen => ImGuiKey.PrintScreen,
		Key.Pause => ImGuiKey.Pause,
		Key.Minus => ImGuiKey.Minus,
		Key.Equal => ImGuiKey.Equal,
		Key.LeftBracket => ImGuiKey.LeftBracket,
		Key.RightBracket => ImGuiKey.RightBracket,
		Key.Backslash => ImGuiKey.Backslash,
		Key.Semicolon => ImGuiKey.Semicolon,
		Key.Apostrophe => ImGuiKey.Apostrophe,
		Key.Grave => ImGuiKey.GraveAccent,
		Key.Comma => ImGuiKey.Comma,
		Key.Period => ImGuiKey.Period,
		Key.Slash => ImGuiKey.Slash,
		Key.KeypadDivide => ImGuiKey.KeypadDivide,
		Key.KeypadMultiply => ImGuiKey.KeypadMultiply,
		Key.KeypadSubtract => ImGuiKey.KeypadSubtract,
		Key.KeypadAdd => ImGuiKey.KeypadAdd,
		Key.KeypadDecimal => ImGuiKey.KeypadDecimal,
		Key.KeypadEnter => ImGuiKey.KeypadEnter,
		Key.KeypadEqual => ImGuiKey.KeypadEqual,
		_ => ImGuiKey.None,
	};
}
