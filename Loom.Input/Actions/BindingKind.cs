namespace Loom.Input;

/// <summary>What an <see cref="InputBinding"/> reads and the shape of its contribution.</summary>
public enum BindingKind : byte
{
	/// <summary>A single button-like source (key / mouse button / gamepad button): contributes 1 when held.</summary>
	Button,

	/// <summary>A single analog axis (a <see cref="GamepadAxis"/>): contributes its level.</summary>
	Axis,

	/// <summary>The horizontal scroll-wheel delta.</summary>
	MouseWheelX,

	/// <summary>The vertical scroll-wheel delta.</summary>
	MouseWheelY,

	/// <summary>The gamepad trigger pair as one axis: right trigger minus left trigger.</summary>
	Triggers,

	/// <summary>Two buttons forming a 1-D axis: <c>Code</c> is the negative, <c>Code2</c> the positive.</summary>
	Composite1D,

	/// <summary>Four buttons forming a 2-D vector: <c>Code</c> up, <c>Code2</c> down, <c>Code3</c> left, <c>Code4</c> right.</summary>
	Composite2D,

	/// <summary>A gamepad stick as a 2-D vector: <c>Code</c> is the X axis, <c>Code2</c> the Y axis.</summary>
	Stick,
}
