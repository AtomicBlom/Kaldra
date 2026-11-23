namespace BinaryVibrance.Kaldra.Input;

/// <summary>Which shape of value an action resolves to.</summary>
public enum ActionKind : byte
{
	/// <summary>A digital on/off action with press/release edges.</summary>
	Button,

	/// <summary>A scalar (1-D) analog action.</summary>
	Axis,

	/// <summary>A 2-D vector action (e.g. movement).</summary>
	Vector,
}
