using System.Numerics;

namespace Loom.Input;

/// <summary>
/// A single active touch contact in a frame's snapshot: its stable <see cref="Id"/> (constant for the life of
/// the contact), its current surface <see cref="Position"/> in pixels, and its lifecycle <see cref="Phase"/>.
/// </summary>
/// <param name="Id">Stable contact id for the life of the touch.</param>
/// <param name="Position">Current surface position in pixels.</param>
/// <param name="Phase">Lifecycle phase this frame.</param>
public readonly record struct TouchPoint(int Id, Vector2 Position, TouchPhase Phase);
