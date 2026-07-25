namespace Loom.Input;

/// <summary>The shape of a <see cref="ResponseCurve"/> applied to an analog value.</summary>
public enum ResponseCurveKind : byte
{
	/// <summary>Identity — the value passes through unchanged.</summary>
	Linear,

	/// <summary>Squared response (exponent 2): finer control near zero, faster near full deflection.</summary>
	Squared,

	/// <summary>Cubic response (exponent 3): an even gentler centre than <see cref="Squared"/>.</summary>
	Cubic,

	/// <summary>An arbitrary exponent supplied by <see cref="ResponseCurve.Exponent"/>.</summary>
	Power,
}
