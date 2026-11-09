using System.Numerics;

namespace Loom.Input;

/// <summary>
/// A single touch contact in a frame's snapshot: its stable <see cref="Id"/> (constant for the life of the contact),
/// its surface <see cref="Position"/> in pixels now and at the start of the frame, where it first came down, and its
/// lifecycle <see cref="Phase"/>.
/// </summary>
/// <remarks>
/// <see cref="TouchPhase.Began"/> lasts the one frame the contact came down in, then it is <see cref="TouchPhase.Moved"/>
/// in any frame it moves and <see cref="TouchPhase.Stationary"/> in any it doesn't, until it is
/// <see cref="TouchPhase.Ended"/> or <see cref="TouchPhase.Canceled"/> for the one frame it goes, then gone. A tap quick
/// enough to come down and lift within one frame is reported once, as ended, with <see cref="BeganThisFrame"/> set.
/// </remarks>
/// <param name="Id">Stable contact id for the life of the touch.</param>
/// <param name="Position">Surface position in pixels, as of this frame.</param>
/// <param name="Phase">Lifecycle phase this frame.</param>
/// <param name="Previous">Surface position at the start of this frame: where it was last frame, or where it came down.</param>
/// <param name="Start">Where the contact came down.</param>
/// <param name="BeganThisFrame">Whether the contact came down this frame, whatever its phase now.</param>
public readonly record struct TouchPoint(int Id, Vector2 Position, TouchPhase Phase, Vector2 Previous, Vector2 Start, bool BeganThisFrame)
{
	/// <summary>How far the contact moved this frame.</summary>
	public Vector2 Delta => Position - Previous;

	/// <summary>Whether the contact lifted or was taken away this frame.</summary>
	public bool Gone => Phase is TouchPhase.Ended or TouchPhase.Canceled;
}
