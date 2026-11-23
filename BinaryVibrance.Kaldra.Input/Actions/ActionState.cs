using System.Numerics;

namespace BinaryVibrance.Kaldra.Input;

/// <summary>
/// The resolved value of one action for a frame, produced by <see cref="InputActions.Resolve"/> from an
/// <see cref="InputSnapshot"/>. Which fields are meaningful depends on the action's kind: buttons use
/// <see cref="Down"/>/<see cref="Pressed"/>/<see cref="Released"/>, axes use <see cref="Value"/>, and vectors
/// use <see cref="Vector"/>.
/// </summary>
public readonly record struct ActionState
{
	/// <summary>Whether a button action is currently held.</summary>
	public bool Down { get; init; }

	/// <summary>Whether a button action transitioned to held this frame.</summary>
	public bool Pressed { get; init; }

	/// <summary>Whether a button action transitioned to released this frame.</summary>
	public bool Released { get; init; }

	/// <summary>The resolved scalar for an axis action.</summary>
	public float Value { get; init; }

	/// <summary>The resolved vector for a vector action.</summary>
	public Vector2 Vector { get; init; }
}
