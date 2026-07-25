namespace Loom.Input;

/// <summary>
/// A set of input device families, used by an input layer to <see cref="InputContext.Block"/> coarse categories
/// of input from the layers beneath it. <see cref="Pointer"/> is the abstract family every <c>pointer*</c> event
/// belongs to (mouse, pen, or touch); a mouse move is therefore both <see cref="Mouse"/> and <see cref="Pointer"/>
/// and a touch is both <see cref="Touch"/> and <see cref="Pointer"/>, so blocking <see cref="Pointer"/> drops all
/// of them while blocking <see cref="Mouse"/> drops only the mouse.
/// </summary>
[Flags]
public enum InputDevice
{
	None = 0,
	Keyboard = 1 << 0,
	Mouse = 1 << 1,
	Gamepad = 1 << 2,
	Touch = 1 << 3,
	Pointer = 1 << 4,

	All = Keyboard | Mouse | Gamepad | Touch | Pointer,
}
