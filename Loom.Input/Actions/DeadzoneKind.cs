namespace Loom.Input;

/// <summary>How a <see cref="Deadzone"/> is applied to an analog input.</summary>
public enum DeadzoneKind : byte
{
	/// <summary>No deadzone; the value passes through unchanged.</summary>
	None,

	/// <summary>Circular: the vector's <i>magnitude</i> is gated and rescaled, preserving direction. Best for
	/// sticks, where drift should be ignored in every direction equally.</summary>
	Radial,

	/// <summary>Per-axis: each component is gated and rescaled independently. Suits inputs treated as two
	/// separate 1-D axes.</summary>
	Axial,
}
