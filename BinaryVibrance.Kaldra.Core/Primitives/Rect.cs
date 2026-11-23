using System.Numerics;
using BinaryVibrance.Kaldra.Core.Geometry;

namespace BinaryVibrance.Kaldra.Core.Primitives;

/// <summary>
/// An axis-aligned rectangle in pixels — neutral geometry (no graphics-API types), used for a draw command's
/// scissor (<see cref="Hosting.DrawCommand.ClipRect"/>) and reused by the <c>Vysara</c> layout/hit-test code.
/// Stored as origin + size so a zero-size rect is unambiguous; <see cref="Right"/>/<see cref="Bottom"/> are
/// derived. Lives in <c>BinaryVibrance.Kaldra.Core</c> beside <see cref="Vertex"/> as managed value data.
/// </summary>
/// <param name="X">Left edge (surface pixels).</param>
/// <param name="Y">Top edge (surface pixels).</param>
/// <param name="Width">Width in pixels.</param>
/// <param name="Height">Height in pixels.</param>
public readonly record struct Rect(float X, float Y, float Width, float Height)
{
	/// <summary>An empty rect at the origin.</summary>
	public static Rect Zero => new(0f, 0f, 0f, 0f);

	/// <summary>Right edge (<see cref="X"/> + <see cref="Width"/>).</summary>
	public float Right => X + Width;

	/// <summary>Bottom edge (<see cref="Y"/> + <see cref="Height"/>).</summary>
	public float Bottom => Y + Height;

	/// <summary>True when this rect encloses zero area.</summary>
	public bool IsEmpty => Width <= 0f || Height <= 0f;

	/// <summary>True if <paramref name="p"/> lies within this rect (left/top inclusive, right/bottom exclusive).</summary>
	public bool Contains(Vector2 p) => p.X >= X && p.X < Right && p.Y >= Y && p.Y < Bottom;

	/// <summary>The overlap of this rect with <paramref name="other"/>; <see cref="IsEmpty"/> when they do not overlap.</summary>
	public Rect Intersect(in Rect other)
	{
		var x = MathF.Max(X, other.X);
		var y = MathF.Max(Y, other.Y);
		var right = MathF.Min(Right, other.Right);
		var bottom = MathF.Min(Bottom, other.Bottom);
		return right <= x || bottom <= y ? Zero : new Rect(x, y, right - x, bottom - y);
	}

	public override string ToString() => $"({X:0.###},{Y:0.###} {Width:0.###}x{Height:0.###})";
}
