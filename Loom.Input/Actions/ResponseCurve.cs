namespace Loom.Input;

/// <summary>
/// A response curve applied to an analog value after its deadzone — a processor that reshapes how deflection
/// maps to output while preserving sign. It raises the input's magnitude to an exponent, so a squared or cubic
/// curve gives finer control near centre without capping the maximum.
/// </summary>
public readonly record struct ResponseCurve
{
	/// <summary>The curve shape.</summary>
	public ResponseCurveKind Kind { get; init; }

	/// <summary>The exponent for <see cref="ResponseCurveKind.Power"/> (ignored by the fixed shapes).</summary>
	public float Exponent { get; init; }

	/// <summary>Identity response.</summary>
	public static ResponseCurve Linear => new() { Kind = ResponseCurveKind.Linear, Exponent = 1f };

	/// <summary>Squared response.</summary>
	public static ResponseCurve Squared => new() { Kind = ResponseCurveKind.Squared, Exponent = 2f };

	/// <summary>Cubic response.</summary>
	public static ResponseCurve Cubic => new() { Kind = ResponseCurveKind.Cubic, Exponent = 3f };

	/// <summary>A response with an arbitrary exponent.</summary>
	public static ResponseCurve Power(float exponent) => new() { Kind = ResponseCurveKind.Power, Exponent = exponent };

	/// <summary>Applies the curve to <paramref name="value"/>, preserving its sign.</summary>
	public float Apply(float value)
	{
		var exponent = Kind switch
		{
			ResponseCurveKind.Squared => 2f,
			ResponseCurveKind.Cubic => 3f,
			ResponseCurveKind.Power => Exponent,
			ResponseCurveKind.Linear => 1f,
			_ => 1f,
		};

		if (exponent == 1f)
			return value;

		return MathF.Sign(value) * MathF.Pow(MathF.Abs(value), exponent);
	}
}
