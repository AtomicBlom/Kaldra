using System.Numerics;

using Loom.Core;

namespace Loom.UI;

/// <summary>
/// Loom's default <see cref="IUICompositor"/> (PlanningUI.md §D2.5): walks a surface's composition tree, folds
/// transform/clip/opacity into a pre-transformed vertex/command stream, and exposes it as neutral
/// <see cref="UIDrawData"/> for the backend's UI renderer. Phase B emits the rectangle family (fills + borders);
/// glyphs/images arrive with MSDF text + images (MU1). Output is asserted on CPU via <see cref="UIDrawData.Dump"/>
/// — the primary test seam, no backend needed.
/// </summary>
public sealed class DrawListCompositor : IUICompositor
{
	private readonly Dictionary<int, UIDrawData> _surfaces = [];
	private readonly Dictionary<int, uint> _revisions = [];
	private readonly FontAtlas _font;

	/// <param name="font">
	/// The atlas glyph runs lay out against; defaults to <see cref="FontAtlas.Default"/> (the embedded MU1 font).
	/// Must be the same atlas the renderer uploaded via <c>EnableUI(font.Texture)</c> so UVs line up.
	/// </param>
	public DrawListCompositor(FontAtlas? font = null) => _font = font ?? FontAtlas.Default;

	public void Composite(RootVisual root, UISurfaceId surface)
	{
		var list = new UIDrawList(root.SurfaceSize);
		Walk(list, root, Matrix3x2.Identity, 1f, new Rect(0, 0, root.SurfaceSize.X, root.SurfaceSize.Y));
		var rev = _revisions.TryGetValue(surface.Value, out var r) ? r + 1 : 1u;
		_revisions[surface.Value] = rev;
		_surfaces[surface.Value] = list.Build(root.IsHdrSurface, rev);
	}

	/// <summary>The last <see cref="UIDrawData"/> produced for <paramref name="surface"/> (handed to FrameInputs.UISurfaces).</summary>
	public UIDrawData LastDraw(UISurfaceId surface) => _surfaces.TryGetValue(surface.Value, out var d) ? d : UIDrawData.Empty;

	private void Walk(UIDrawList list, Visual v, Matrix3x2 parent, float opacity, Rect clip)
	{
		var matrix = v.Transform * parent;
		var alpha = opacity * v.Opacity;
		var localClip = clip;
		if (v.Clip is { } c)
			localClip = clip.Intersect(TransformRect(c, matrix));

		if (v is SpriteVisual sprite)
		{
			switch (sprite.Content)
			{
				case RectContent rc:
					{
						var rect = new Rect(0, 0, rc.Size.X, rc.Size.Y);
						if (rc.Fill is { } fill)
						{
							var kind = rc.CornerRadius > 0 ? UIPrimKind.RoundedRect : UIPrimKind.Fill;
							// params = (cornerRadius, borderThickness, sizeX, sizeY) — the layout ui.slang's analytic rounded-box
							// SDF reads (uv is 0..1 across the rect; the shader scales by params.zw to local pixels). Fill ignores it.
							list.AddRect(rect, matrix, fill.ToPremultiplied(alpha), kind, new Vector4(rc.CornerRadius, 0f, rc.Size.X, rc.Size.Y), localClip);
						}
						if (rc.BorderBrush is { } bb && rc.BorderThickness > 0)
							list.AddRect(rect, matrix, bb.ToPremultiplied(alpha), UIPrimKind.Border, new Vector4(rc.CornerRadius, rc.BorderThickness, rc.Size.X, rc.Size.Y), localClip);
						break;
					}
				case GlyphRunContent gr:
					list.AddGlyphRun(gr.Text, gr.FontSizePx, gr.Color.ToPremultiplied(alpha), _font, matrix, localClip);
					break;
			}
		}

		foreach (var child in v.Children)
			Walk(list, child, matrix, alpha, localClip);
	}

	// Axis-aligned bound of a local rect under an affine transform (exact for the translate/scale transforms used
	// by layout; a conservative AABB for rotation — fine for clipping).
	private static Rect TransformRect(in Rect r, in Matrix3x2 m)
	{
		Span<Vector2> p =
		[
			Vector2.Transform(new Vector2(r.X, r.Y), m),
			Vector2.Transform(new Vector2(r.Right, r.Y), m),
			Vector2.Transform(new Vector2(r.Right, r.Bottom), m),
			Vector2.Transform(new Vector2(r.X, r.Bottom), m),
		];
		float minX = p[0].X, minY = p[0].Y, maxX = p[0].X, maxY = p[0].Y;
		for (var i = 1; i < 4; i++)
		{
			minX = MathF.Min(minX, p[i].X); minY = MathF.Min(minY, p[i].Y);
			maxX = MathF.Max(maxX, p[i].X); maxY = MathF.Max(maxY, p[i].Y);
		}
		return new Rect(minX, minY, maxX - minX, maxY - minY);
	}
}
