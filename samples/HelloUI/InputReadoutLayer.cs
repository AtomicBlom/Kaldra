using Loom.Input;

namespace HelloUI;

/// <summary>
/// The top of the sample's input stack, blocking nothing: it shows on the page the last key, character, mouse button and
/// controller event the app received and the first controller's sticks, rumbles a controller when its bottom face button
/// goes down, and treats Escape (which Back is on Android) as leaving the app.
/// </summary>
internal sealed class InputReadoutLayer(HelloViewModel viewModel, Action exit) : IInputLayer
{
	private readonly float[] _axes = new float[6];
	private bool _padSeen;

	public void Process(ref InputContext ctx)
	{
		var axesChanged = false;
		foreach (ref readonly var e in ctx.Incoming)
		{
			switch (e.Kind)
			{
				case InputEventKind.KeyDown:
					viewModel.LastInput = $"Key down: {e.Key}";
					if (e.Key == Key.Escape)
						exit();
					break;
				case InputEventKind.KeyUp:
					viewModel.LastInput = $"Key up: {e.Key}";
					break;
				case InputEventKind.Text:
					viewModel.LastInput = $"Typed: '{char.ConvertFromUtf32((int)e.CodePoint)}'";
					break;
				case InputEventKind.MouseButtonDown when e.Button != MouseButton.Left:
					viewModel.LastInput = $"Mouse down: {e.Button}";
					break;
				case InputEventKind.MouseButtonUp when e.Button != MouseButton.Left:
					viewModel.LastInput = $"Mouse up: {e.Button}";
					break;
				case InputEventKind.GamepadConnected:
					viewModel.LastInput = $"Pad {e.PadIndex} connected";
					_padSeen = true;
					axesChanged = true;
					break;
				case InputEventKind.GamepadDisconnected:
					viewModel.LastInput = $"Pad {e.PadIndex} disconnected";
					break;
				case InputEventKind.GamepadButtonDown:
					viewModel.LastInput = $"Pad {e.PadIndex}: {e.GamepadButton} down";
					if (e.GamepadButton == GamepadButton.South)
						ctx.Snapshot.Rumble(e.PadIndex, 0.8f, 0.4f, 0.3f);
					break;
				case InputEventKind.GamepadButtonUp:
					viewModel.LastInput = $"Pad {e.PadIndex}: {e.GamepadButton} up";
					break;
				case InputEventKind.GamepadAxis when e.PadIndex == 0:
					_axes[(int)e.GamepadAxis] = e.Value;
					axesChanged = true;
					break;
			}
		}
		if (axesChanged && _padSeen)
			viewModel.PadState = $"L ({_axes[0]:0.00}, {_axes[1]:0.00})  R ({_axes[2]:0.00}, {_axes[3]:0.00})  LT {_axes[4]:0.00}  RT {_axes[5]:0.00}";
	}
}
