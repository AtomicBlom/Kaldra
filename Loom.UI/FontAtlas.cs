using System.Reflection;
using System.Text.Json;

using Loom.Assets;
using Loom.Core;
using Loom.Core.Materials;

namespace Loom.UI;

/// <summary>
/// A baked MTSDF font atlas (PlanningUI.md §D3): the decoded atlas page plus per-glyph layout, loaded from the
/// pair an MSDF generator emits — <c>&lt;name&gt;.0.png</c> (RGBA8 MTSDF, RGB = multi-channel SDF, A = true SDF) and
/// <c>&lt;name&gt;.font.json</c> (the msdf-atlas-gen JSON layout: atlas dims + <c>distanceRange</c>, font metrics in
/// EM, and per-glyph plane bounds / atlas UV rect / advance). This JSON shape <em>is</em> the format contract the
/// MUx native generator must emit (PlanningUI.md §D10) — the atlas swaps in with zero changes here.
///
/// The page is decoded <b>linear / UNORM, no mips</b>: MTSDF texels are signed distances, never sRGB colour.
/// Plane bounds are EM units in a y-down, baseline-at-origin frame (top is above the baseline → negative y), so a
/// glyph at pen <c>(penX, baselineY)</c> rendered at <c>fontSizePx</c> spans
/// <c>[penX + plane.left·s, penX + plane.right·s] × [baselineY + plane.top·s, baselineY + plane.bottom·s]</c>.
/// </summary>
public sealed class FontAtlas
{
	private readonly Dictionary<int, Glyph> _glyphs;

	private FontAtlas(TextureData texture, int width, int height, float distanceRange, float bakeSize,
		float lineHeight, float ascender, float descender, Dictionary<int, Glyph> glyphs)
	{
		Texture = texture;
		Width = width;
		Height = height;
		DistanceRange = distanceRange;
		BakeSize = bakeSize;
		LineHeight = lineHeight;
		Ascender = ascender;
		Descender = descender;
		_glyphs = glyphs;
	}

	/// <summary>The decoded MTSDF page as neutral <see cref="TextureData"/> (linear RGBA8 / UNORM, no mips) — hand to <see cref="UIPainter.Create"/>.</summary>
	public TextureData Texture { get; }

	/// <summary>Atlas page dimensions in texels.</summary>
	public int Width { get; }
	public int Height { get; }

	/// <summary>The SDF distance range baked into the atlas, in atlas texels (msdf-atlas-gen <c>pxRange</c>).</summary>
	public float DistanceRange { get; }

	/// <summary>The EM size the atlas was rasterized at, in texels (msdf-atlas-gen <c>atlas.size</c>).</summary>
	public float BakeSize { get; }

	/// <summary>Line advance in EM units (multiply by the on-screen font px size for the pixel line height).</summary>
	public float LineHeight { get; }

	/// <summary>Ascender / descender in EM units (y-down: the ascender is above the baseline, so it is negative).</summary>
	public float Ascender { get; }
	public float Descender { get; }

	/// <summary>Look up a glyph by Unicode code point. Returns false for code points outside the baked charset.</summary>
	public bool TryGetGlyph(int codepoint, out Glyph glyph) => _glyphs.TryGetValue(codepoint, out glyph);

	/// <summary>
	/// The MTSDF distance range expressed in <em>screen</em> pixels for a glyph run drawn at <paramref name="fontSizePx"/>.
	/// Fed to the shader as the smoothstep scale (§D3.4); scales with the on-screen size vs the baked EM size.
	/// </summary>
	public float ScreenPxRange(float fontSizePx) => DistanceRange * fontSizePx / BakeSize;

	/// <summary>Advance width of a code point in EM (falls back to the space glyph's advance for unknown code points).</summary>
	public float Advance(int codepoint)
	{
		if (_glyphs.TryGetValue(codepoint, out var g))
			return g.Advance;
		return _glyphs.TryGetValue(' ', out var space) ? space.Advance : 0.5f;
	}

	/// <summary>Measure a single-line string's advance width in EM units (no shaping/kerning — that is MU5).</summary>
	public float MeasureWidth(ReadOnlySpan<char> text)
	{
		var w = 0f;
		foreach (var c in text)
			w += Advance(c);
		return w;
	}

	/// <summary>Decode an atlas from its JSON layout + PNG page streams (the test/host entry point).</summary>
	public static FontAtlas Load(Stream json, Stream png)
	{
		var texture = ImageLoader.Decode(png, TextureFormat.Rgba8Unorm, generateMips: false);

		using var doc = JsonDocument.Parse(json);
		var root = doc.RootElement;

		var atlas = root.GetProperty("atlas");
		var distanceRange = atlas.GetProperty("distanceRange").GetSingle();
		var bakeSize = atlas.GetProperty("size").GetSingle();
		var width = atlas.GetProperty("width").GetInt32();
		var height = atlas.GetProperty("height").GetInt32();

		var metrics = root.GetProperty("metrics");
		var lineHeight = metrics.GetProperty("lineHeight").GetSingle();
		var ascender = metrics.GetProperty("ascender").GetSingle();
		var descender = metrics.GetProperty("descender").GetSingle();

		var glyphs = new Dictionary<int, Glyph>();
		foreach (var g in root.GetProperty("glyphs").EnumerateArray())
		{
			var cp = g.GetProperty("unicode").GetInt32();
			var advance = g.GetProperty("advance").GetSingle();

			// Whitespace glyphs (space) carry an advance but no geometry — emit nothing, just advance the pen.
			if (g.TryGetProperty("planeBounds", out var pb) && g.TryGetProperty("atlasBounds", out var ab))
			{
				glyphs[cp] = new Glyph(
					cp, advance, HasGeometry: true,
					pb.GetProperty("left").GetSingle(), pb.GetProperty("top").GetSingle(),
					pb.GetProperty("right").GetSingle(), pb.GetProperty("bottom").GetSingle(),
					ab.GetProperty("left").GetSingle() / width, ab.GetProperty("top").GetSingle() / height,
					ab.GetProperty("right").GetSingle() / width, ab.GetProperty("bottom").GetSingle() / height);
			}
			else
				glyphs[cp] = new Glyph(cp, advance, HasGeometry: false, 0, 0, 0, 0, 0, 0, 0, 0);
		}

		return new FontAtlas(texture, width, height, distanceRange, bakeSize, lineHeight, ascender, descender, glyphs);
	}

	/// <summary>
	/// The engine's single default font — the embedded <c>ui-default</c> (Cascadia Code MTSDF), loaded once on first
	/// use. This is the default "font store": <see cref="Controls.TextBlock"/> measures against it and the default
	/// <see cref="DrawListCompositor"/> lays glyphs out against it, so the atlas the painter uploads
	/// (<c>UIPainter.Create(engine, Default.Texture)</c>) is guaranteed to match what was composited. (UI runs on one
	/// thread, so the lazy init needs no lock.)
	/// </summary>
	public static FontAtlas Default => field ??= LoadEmbeddedDefault();

	/// <summary>Load the engine's embedded default atlas (<c>ui-default</c>, Cascadia Code MTSDF) from this assembly.</summary>
	public static FontAtlas LoadEmbeddedDefault()
	{
		var asm = typeof(FontAtlas).Assembly;
		using var json = OpenResource(asm, "ui-default.font.json");
		using var png = OpenResource(asm, "ui-default.0.png");
		return Load(json, png);
	}

	private static Stream OpenResource(Assembly asm, string logicalName) =>
		asm.GetManifestResourceStream(logicalName)
		?? throw new InvalidOperationException($"Embedded font resource '{logicalName}' not found in {asm.GetName().Name}.");
}
