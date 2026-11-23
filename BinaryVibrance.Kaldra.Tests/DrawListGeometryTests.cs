using BinaryVibrance.Kaldra.Core.Geometry;
using BinaryVibrance.Kaldra.Core.Hosting;
using BinaryVibrance.Kaldra.Core.Primitives;

namespace BinaryVibrance.Kaldra.Tests;

/// <summary>
/// The neutral draw-list geometry every backend records from: a command's scissor, a group-opacity layer's bounds and
/// render area, and the layer composite quad.
/// <para>
/// Worth pinning device-free precisely because it <em>was</em> three implementations that disagreed. All three
/// backends rounded a scissor differently — one subtracted in float and truncated the extent, the other two truncated
/// each edge — which is invisible under a 4/255 parity tolerance and would stay invisible while quietly costing a
/// pixel column of a clipped control. These tests fix the rounding rules so a fourth backend inherits them rather
/// than re-deriving them.
/// </para>
/// </summary>
public class DrawListGeometryTests
{
	[Test]
	public void Scissor_keeps_the_pixels_whose_centres_the_clip_covers()
	{
		// A fractional origin whose fraction exceeds the extent's: x 10.5..15.4. Each edge is rounded on its own, so the
		// pixels whose centres lie inside are kept, 10 (its centre on the near edge) to 14; subtracting first and
		// rounding the difference would lose one.
		DrawListGeometry.Scissor(new Rect(10.5f, 10.5f, 4.9f, 4.9f), 100, 100).ShouldBe((10, 10, 5, 5));

		// Edges between pixel rows: x 10.4..30.4 keeps the centres 10.5 to 29.5, y 10.6..20.6 the centres 11.5 to 20.5.
		// Truncating instead keeps row 10 (centre 10.5, outside) and loses row 20 (centre 20.5, inside).
		DrawListGeometry.Scissor(new Rect(10.4f, 10.6f, 20f, 10f), 100, 100).ShouldBe((10, 11, 20, 10));

		// A centre on the far edge is out.
		DrawListGeometry.Scissor(new Rect(0f, 0f, 10.5f, 10.5f), 100, 100).ShouldBe((0, 0, 10, 10));
	}

	[Test]
	public void Scissor_clamps_to_the_target()
	{
		DrawListGeometry.Scissor(new Rect(-20f, -5f, 200f, 30f), 64, 64).ShouldBe((0, 0, 64, 25));
		DrawListGeometry.Scissor(new Rect(50f, 50f, 100f, 100f), 64, 64).ShouldBe((50, 50, 14, 14));
	}

	[Test]
	public void Scissor_reports_a_fully_clipped_command_as_empty()
	{
		// Off the target entirely: the far edge clamps below the origin, so the extent goes negative. The caller has to
		// see that and skip — a backend handing a negative extent to an unsigned API wraps it into an enormous one.
		var (_, _, width, height) = DrawListGeometry.Scissor(new Rect(200f, 200f, 10f, 10f), 64, 64);
		width.ShouldBeLessThanOrEqualTo(0);
		height.ShouldBeLessThanOrEqualTo(0);
	}

	[Test]
	public void Layer_bounds_grow_by_a_pixel_and_clamp_to_the_surface()
	{
		// The slop exists so a rounded edge cannot clip the antialiased fringe of whatever sits on the group boundary.
		DrawListGeometry.LayerBounds(new Rect(20f, 30f, 40f, 50f), 200f, 200f)
			.ShouldBe((19f, 29f, 61f, 81f));
		// Grown past the surface, the slop is clamped away rather than addressing outside the target.
		DrawListGeometry.LayerBounds(new Rect(0f, 0f, 200f, 200f), 200f, 200f)
			.ShouldBe((0f, 0f, 200f, 200f));
	}

	[Test]
	public void Layer_bounds_off_the_surface_fall_back_to_the_whole_surface()
	{
		// Bounds that clamp away to nothing would give the layer a zero-area render target and a degenerate composite
		// quad, so the conservative answer is everything — a correct frame at a cost, rather than a missing subtree.
		DrawListGeometry.LayerBounds(new Rect(500f, 500f, 10f, 10f), 128f, 64f).ShouldBe((0f, 0f, 128f, 64f));
		DrawListGeometry.LayerBounds(new Rect(-40f, -40f, 10f, 10f), 128f, 64f).ShouldBe((0f, 0f, 128f, 64f));
	}

	[Test]
	public void A_zero_size_group_still_gets_its_own_slop_sized_layer()
	{
		// Not the whole-surface fallback: the 1px slop makes even an empty group a valid 1x1 rect, which costs almost
		// nothing to clear and composites nothing. Falling back to the surface here would clear a screen per empty group.
		DrawListGeometry.LayerBounds(Rect.Zero, 128f, 64f).ShouldBe((0f, 0f, 1f, 1f));
	}

	[Test]
	public void Layer_area_never_covers_less_than_the_bounds_it_came_from()
	{
		// Floor the origin, ceil the far edge: a layer's render/clear area that fell short of its bounds would leave
		// the group's edge pixels unwritten, and the composite samples them.
		DrawListGeometry.LayerArea((10.7f, 20.2f, 30.1f, 40.9f), 1f, 1f, 100, 100)
			.ShouldBe((10, 20, 21, 21));
	}

	[Test]
	public void Layer_area_scales_surface_pixels_to_target_pixels()
	{
		// A list's coordinate space is not the target's when the pass renders at a different resolution.
		DrawListGeometry.LayerArea((10f, 10f, 20f, 20f), 2f, 2f, 100, 100).ShouldBe((20, 20, 20, 20));
	}

	[Test]
	public void Layer_area_clamps_to_the_target()
	{
		DrawListGeometry.LayerArea((-5f, -5f, 500f, 500f), 1f, 1f, 64, 32).ShouldBe((0, 0, 64, 32));
	}

	[Test]
	public void Composite_quad_covers_the_bounds_with_matching_texture_coordinates()
	{
		Span<OverlayVertex> quad = stackalloc OverlayVertex[DrawListGeometry.CompositeQuadVertexCount];
		var tint = Half4.FromStraight(1f, 1f, 1f, 0.5f);
		DrawListGeometry.WriteCompositeQuad((40f, 20f, 120f, 60f), 160f, 80f, tint, quad);

		// Positions span the bounds and the uvs are those bounds as a fraction of the surface — the layer target holds
		// its content where the content is, so a fixed 0..1 would sample the wrong texels for a narrowed layer.
		quad[0].Position.X.ShouldBe(40f);
		quad[0].Position.Y.ShouldBe(20f);
		quad[2].Position.X.ShouldBe(120f);
		quad[2].Position.Y.ShouldBe(60f);
		quad[0].Uv.X.ShouldBe(0.25f);
		quad[0].Uv.Y.ShouldBe(0.25f);
		quad[2].Uv.X.ShouldBe(0.75f);
		quad[2].Uv.Y.ShouldBe(0.75f);

		// Two triangles, and the group tint on every vertex — the fade is one multiply over the whole subtree.
		for (var i = 0; i < DrawListGeometry.CompositeQuadVertexCount; i++)
			quad[i].Color.ShouldBe(tint);
		quad[3].Position.ShouldBe(quad[0].Position);
		quad[4].Position.ShouldBe(quad[2].Position);
	}

	[Test]
	public void Composite_quad_rejects_a_destination_that_cannot_hold_it()
	{
		// Fail loud: a short span would otherwise write a partial triangle list and draw whatever followed it.
		var tooSmall = new OverlayVertex[DrawListGeometry.CompositeQuadVertexCount - 1];
		Should.Throw<ArgumentException>(() => DrawListGeometry.WriteCompositeQuad((0f, 0f, 1f, 1f), 1f, 1f, default, tooSmall));
	}
}
