using System.Numerics;

namespace BinaryVibrance.Kaldra.Input;

/// <summary>
/// A deadzone for analog input: values with magnitude at or below <see cref="Inner"/> read as zero, and the
/// remaining range up to <see cref="Outer"/> is rescaled back to a full [0, 1] so control begins the instant the
/// input clears the deadzone. <see cref="Radial"/> gates a vector's magnitude (direction preserved);
/// <see cref="Axial"/> gates each component separately.
/// </summary>
public readonly record struct Deadzone
{
	/// <summary>How the deadzone is applied.</summary>
	public DeadzoneKind Kind { get; init; }

	/// <summary>The inner threshold below which input reads as zero.</summary>
	public float Inner { get; init; }

	/// <summary>The outer edge that rescales to full deflection.</summary>
	public float Outer { get; init; }

	/// <summary>No deadzone.</summary>
	public static Deadzone None => new() { Kind = DeadzoneKind.None, Inner = 0f, Outer = 1f };

	/// <summary>A circular deadzone gating vector magnitude, preserving direction.</summary>
	public static Deadzone Radial(float inner, float outer = 1f) => new() { Kind = DeadzoneKind.Radial, Inner = inner, Outer = outer };

	/// <summary>A per-axis deadzone gating each component independently.</summary>
	public static Deadzone Axial(float inner, float outer = 1f) => new() { Kind = DeadzoneKind.Axial, Inner = inner, Outer = outer };

	/// <summary>Applies the deadzone to a scalar, preserving its sign.</summary>
	public float Apply(float value)
	{
		if (Kind == DeadzoneKind.None)
			return value;

		var magnitude = MathF.Abs(value);
		if (magnitude <= Inner)
			return 0f;

		return MathF.Sign(value) * Rescale(magnitude);
	}

	/// <summary>Applies the deadzone to a 2-D vector.</summary>
	public Vector2 Apply(Vector2 value)
	{
		switch (Kind)
		{
			case DeadzoneKind.Axial:
				return new Vector2(Apply(value.X), Apply(value.Y));
			case DeadzoneKind.Radial:
				var magnitude = value.Length();
				if (magnitude <= Inner)
					return Vector2.Zero;
				return value * (Rescale(magnitude) / magnitude);
			case DeadzoneKind.None:
			default:
				return value;
		}
	}

	// Map a magnitude in (Inner, Outer] onto (0, 1], clamped. Guards a degenerate Inner >= Outer.
	private float Rescale(float magnitude)
	{
		var outer = Outer <= Inner ? Inner + 1f : Outer;
		return Math.Clamp((magnitude - Inner) / (outer - Inner), 0f, 1f);
	}
}
