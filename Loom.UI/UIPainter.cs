using Loom.Core.Geometry;
using Loom.Core.Graph;
using Loom.Core.Hosting;
using Loom.Core.Materials;

namespace Loom.UI;

/// <summary>
/// Renders composited UI draw lists through the public engine API — the bridge between the headless
/// <see cref="UIHost"/> and the render graph, with no backend knowledge. <see cref="Create"/> it once after the
/// engine exists (it registers the <c>ui</c> technique and uploads the white texel + glyph atlas over the public
/// API), keep it for the engine's lifetime, and call <see cref="Draw"/> from a pass that blends the target — it
/// pulls the named list from <see cref="IRenderContext.DrawLists"/> and submits it via
/// <see cref="IRenderContext.DrawList{TVertex}"/>. A painter is a stable handle bundle, so a pass closure may
/// capture it (unlike per-frame state, which is pulled from the context).
/// </summary>
public sealed class UIPainter
{
	private const string Technique = "ui";
	// Paper-white luminance reference for an HDR target: UIPaperWhiteNits / 80. SDR targets pass 1.0.
	private const float HdrPaperWhiteScale = 200f / 80f;

	// The UI textures a command selects between by index: the 1×1 white texel (fills/borders) at 0, the optional
	// MTSDF glyph atlas at 1. Held in one reused array so each Draw binds the span without a per-frame allocation.
	private readonly Texture[] _textures;

	private UIPainter(Texture white, Texture? font) =>
		_textures = font is { } f ? [white, f] : [white];

	/// <summary>
	/// Register the <c>ui</c> technique and upload the UI textures over the public engine API, returning a painter to
	/// keep. <paramref name="fontAtlas"/> is the MTSDF glyph atlas (neutral <see cref="TextureData"/>, linear/UNORM)
	/// the compositor laid glyphs out against; null sets up a painter for rect/border-only surfaces (no text).
	/// </summary>
	public static UIPainter Create(IEngine engine, TextureData? fontAtlas = null)
	{
		// A "ui" technique baked from ui.slang like any other: premultiplied-alpha blend, a single linear-filtered
		// sampler slot, the 2D overlay vertex layout, and the small all-stage list push the per-command tag rides in.
		engine.DefineTechnique(Technique, new TechniqueDescriptor(Technique, HasVertexInput: true,
			Blend: BlendMode.PremultipliedAlpha, LinearFilter: true, Layout: VertexLayout.Overlay, HasListPush: true));
		// Engine-owned textures (both linear/UNORM, never sRGB-decoded): a 1×1 white texel for fills/borders, the
		// optional glyph atlas for text.
		var white = engine.CreateTexture(TextureData.FromRgba8(1, 1, [255, 255, 255, 255], TextureFormat.Rgba8Unorm, generateMips: false));
		var font = fontAtlas is not null ? engine.CreateTexture(fontAtlas) : (Texture?)null;
		return new UIPainter(white, font);
	}

	/// <summary>
	/// Submit the draw list registered under <paramref name="id"/> in this frame's
	/// <see cref="IRenderContext.DrawLists"/>. A no-op when the frame carries no list for that id or it is empty.
	/// Call from a pass that blends the target (the screen-space overlay) or writes an in-world quad.
	/// </summary>
	public void Draw(IRenderContext ctx, DrawListId id)
	{
		var lists = ctx.DrawLists;
		// Indexed loop, not foreach/LINQ: avoids an enumerator allocation on the per-frame render path (§8: 0 GC).
		DrawListData? draw = null;
		for (var i = 0; i < lists.Count; i++)
		{
			if (lists[i].Id == id)
			{
				draw = lists[i].Data;
				break;
			}
		}

		if (draw is null || draw.Commands.IsEmpty)
			return;

		// The shared per-list push: surface-pixel → clip scale/translate, then the HDR paper-white lift. isHdr is a
		// uint the shader tests against 0, so its exact bits ride the float slot; the per-command tag is the primitive kind.
		Span<float> push = stackalloc float[6];
		push[0] = 2.0f / draw.CoordinateSize.X;
		push[1] = 2.0f / draw.CoordinateSize.Y;
		push[2] = -1.0f;
		push[3] = -1.0f;
		push[4] = draw.IsHdrTarget ? HdrPaperWhiteScale : 1.0f;
		push[5] = BitConverter.UInt32BitsToSingle(draw.IsHdrTarget ? 1u : 0u);

		ctx.DrawList<OverlayVertex>(Technique, _textures, draw.Vertices.Span, draw.Indices.Span, draw.Commands.Span, push);
	}
}
