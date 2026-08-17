using System.Numerics;
using Loom.Core.Diagnostics;
using Loom.Core.Geometry;
using Loom.Core.Primitives;

namespace Loom.Core.Hosting;

/// <summary>
/// The neutral half of recording an <see cref="IRenderContext.DrawList{TVertex}"/> submission: the tag values a
/// producer encodes into a <see cref="DrawCommand"/>, and the geometry a backend derives from one — a command's
/// scissor, a group-opacity layer's bounds and render area, and the quad that composites a resolved layer back.
/// <para>
/// None of it touches a graphics API: a clip rect, a scale, and pixel extents in, integers and
/// <see cref="OverlayVertex"/> out. It lives here rather than three times in the backends because these are
/// decisions about what the <em>draw list</em> means, not about how a particular API is driven — and because three
/// copies of a rounding rule is three chances for them to disagree by a pixel, which is exactly what they did.
/// </para>
/// </summary>
public static class DrawListGeometry
{
	/// <summary>
	/// <see cref="DrawCommand.Tag"/> marking the start of a group-opacity layer: the commands up to its matching
	/// <see cref="TagEndLayer"/> render into an offscreen target at full alpha, which is then composited back once at
	/// the group's alpha — so a fading subtree fades as one image and overlapping children do not double-darken.
	/// <para>
	/// The marker carries no indices and never reaches a fragment shader. Its <see cref="DrawCommand.ClipRect"/> is
	/// the group's bounds and its first vertex's colour is the premultiplied group tint, both of which the backend
	/// reads rather than drawing.
	/// </para>
	/// </summary>
	public const uint TagBeginLayer = 5;

	/// <summary>Closes the layer opened by the most recent <see cref="TagBeginLayer"/>.</summary>
	public const uint TagEndLayer = 6;

	/// <summary>
	/// The tag a backend pushes for the layer composite itself — the shader branch that samples a texture and
	/// multiplies by the vertex colour.
	/// </summary>
	public const uint KindImage = 4;

	/// <summary>
	/// Push-block slot carrying "the target is HDR", which a technique uses to lift paper-white. Forced to zero for a
	/// layer composite: the layer's own primitives already lifted, and the lift commutes with a premultiplied
	/// alpha-over, so lifting again would double it.
	/// </summary>
	public const int HdrPushSlot = 5;

	/// <summary>Vertices <see cref="WriteCompositeQuad"/> writes — two triangles, no index buffer.</summary>
	public const int CompositeQuadVertexCount = 6;

	/// <summary>
	/// How deeply group-opacity layers may nest. Each open layer needs its own pooled offscreen target for as long as
	/// it is open, so the depth is bounded; this is the number every backend's pool is sized for, held in common so a
	/// list that renders on one renders on all.
	/// </summary>
	public const int MaxLayerNesting = 8;

	/// <summary>
	/// Report a list that nests group-opacity layers past <see cref="MaxLayerNesting"/>, at most once per
	/// <paramref name="warned"/> flag — a backend keeps one per render context, so a pathological list says so once
	/// instead of every frame.
	/// <para>
	/// A warning rather than a throw, which is the opposite of how Loom usually treats an over-promised API. The
	/// difference is where it happens: this is discovered <em>mid-command-stream</em>, with a render scope open and a
	/// layer stack half-unwound, and throwing there abandons a command buffer in a state nothing can recover. A subtree
	/// that renders unfaded at absurd nesting is a smaller failure than that. Derive-time over-promises and unmapped
	/// enum values still throw; this one cannot.
	/// </para>
	/// </summary>
	public static void WarnLayerNestingExceeded(ref bool warned, int depth)
	{
		if (warned)
			return;
		warned = true;
		LoomLog.Warning(
			$"a draw list nests group-opacity layers {depth} deep but at most {MaxLayerNesting} are supported; " +
			"the subtrees past that render unfaded. Flatten the nested opacities — a group alpha multiplies, so one " +
			"container carrying the product renders the same thing with one layer.");
	}

	/// <summary>
	/// A command's scissor rectangle in target pixels, clamped to the target. Each edge is truncated independently,
	/// so the rectangle covers exactly the pixels whose origin falls inside the clip. A width or height of zero or
	/// less means the command is fully clipped and must be skipped — the caller has to check, because a backend that
	/// hands a negative extent to an unsigned API wraps it into an enormous one.
	/// </summary>
	public static (int X, int Y, int Width, int Height) Scissor(Rect clip, int targetWidth, int targetHeight)
	{
		var x = (int)MathF.Max(0f, clip.X);
		var y = (int)MathF.Max(0f, clip.Y);
		var right = (int)MathF.Min(targetWidth, clip.X + clip.Width);
		var bottom = (int)MathF.Min(targetHeight, clip.Y + clip.Height);
		return (x, y, right - x, bottom - y);
	}

	/// <summary>
	/// A group-opacity layer's bounds in the draw list's own coordinate space, clamped to the surface and grown by a
	/// pixel so a rounded edge cannot clip the antialiased fringe of whatever sits against the group's boundary.
	/// Degenerate bounds fall back to the whole surface, which is the conservative answer.
	/// </summary>
	public static (float X0, float Y0, float X1, float Y1) LayerBounds(Rect clip, float surfaceWidth, float surfaceHeight)
	{
		const float Slop = 1f;
		var x0 = MathF.Max(0f, clip.X - Slop);
		var y0 = MathF.Max(0f, clip.Y - Slop);
		var x1 = MathF.Min(surfaceWidth, clip.X + clip.Width + Slop);
		var y1 = MathF.Min(surfaceHeight, clip.Y + clip.Height + Slop);
		return x1 > x0 && y1 > y0 ? (x0, y0, x1, y1) : (0f, 0f, surfaceWidth, surfaceHeight);
	}

	/// <summary>
	/// The same bounds as a target-pixel rectangle: the origin floored and the far edge ceiled so the rectangle never
	/// covers less than the bounds it came from, then clamped to the target. A backend narrows a layer's render or
	/// clear area to this, because a layer costs a scope break and a clear and a tile-based GPU prices both by area —
	/// a checkbox fading in should not clear a screen's worth of tiles.
	/// </summary>
	public static (int X, int Y, int Width, int Height) LayerArea(
		(float X0, float Y0, float X1, float Y1) bounds, float scaleX, float scaleY, int targetWidth, int targetHeight)
	{
		var x = Math.Clamp((int)MathF.Floor(bounds.X0 * scaleX), 0, targetWidth);
		var y = Math.Clamp((int)MathF.Floor(bounds.Y0 * scaleY), 0, targetHeight);
		var right = Math.Clamp((int)MathF.Ceiling(bounds.X1 * scaleX), x, targetWidth);
		var bottom = Math.Clamp((int)MathF.Ceiling(bounds.Y1 * scaleY), y, targetHeight);
		return (x, y, right - x, bottom - y);
	}

	/// <summary>
	/// Write the two triangles that composite a resolved layer back into its parent, into <paramref name="quad"/>
	/// (which must hold <see cref="CompositeQuadVertexCount"/> vertices).
	/// <para>
	/// The quad covers the group's bounds rather than the whole surface, and its texture coordinates are therefore
	/// those bounds as a fraction of the surface rather than a fixed 0..1: narrowing a layer's render area restricted
	/// which pixels were written, it did not move the content within the target.
	/// </para>
	/// </summary>
	/// <param name="bounds">The layer's bounds in the list's coordinate space, from <see cref="LayerBounds"/>.</param>
	/// <param name="surfaceWidth">Width of the list's coordinate space, for the texture coordinates.</param>
	/// <param name="surfaceHeight">Height of the list's coordinate space, for the texture coordinates.</param>
	/// <param name="tint">The group's premultiplied alpha tint, applied to every vertex.</param>
	/// <param name="quad">Destination for the vertices.</param>
	public static void WriteCompositeQuad(
		(float X0, float Y0, float X1, float Y1) bounds, float surfaceWidth, float surfaceHeight, Half4 tint, Span<OverlayVertex> quad)
	{
		if (quad.Length < CompositeQuadVertexCount)
			throw new ArgumentException($"A layer composite needs {CompositeQuadVertexCount} vertices but the destination holds {quad.Length}.", nameof(quad));

		var custom = default(Vector4);
		var (x0, y0, x1, y1) = bounds;
		var u0 = x0 / surfaceWidth;
		var v0 = y0 / surfaceHeight;
		var u1 = x1 / surfaceWidth;
		var v1 = y1 / surfaceHeight;
		quad[0] = new OverlayVertex(new Vector2(x0, y0), new Vector2(u0, v0), tint, custom);
		quad[1] = new OverlayVertex(new Vector2(x1, y0), new Vector2(u1, v0), tint, custom);
		quad[2] = new OverlayVertex(new Vector2(x1, y1), new Vector2(u1, v1), tint, custom);
		quad[3] = new OverlayVertex(new Vector2(x0, y0), new Vector2(u0, v0), tint, custom);
		quad[4] = new OverlayVertex(new Vector2(x1, y1), new Vector2(u1, v1), tint, custom);
		quad[5] = new OverlayVertex(new Vector2(x0, y1), new Vector2(u0, v1), tint, custom);
	}
}
