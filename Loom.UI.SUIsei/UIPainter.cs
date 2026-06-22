using System.Runtime.InteropServices;
using Loom.Core.Geometry;
using Loom.Core.Graph;
using Loom.Core.Hosting;
using Loom.Core.Materials;
using SUIsei;
// Explicit aliases resolve the DrawListId / DrawListData / DrawCommand name conflicts
// between SUIsei.Drawing and Loom.Core.Hosting.
using DrawListId = SUIsei.Drawing.DrawListId;
using DrawListData = SUIsei.Drawing.DrawListData;
using DrawCommand = SUIsei.Drawing.DrawCommand;
using LoomDrawCommand = Loom.Core.Hosting.DrawCommand;

namespace Loom.UI.SUIsei;

/// <summary>
/// Renders composited UI draw lists through the public engine API — the bridge between the headless
/// <see cref="UIHost"/> and the render graph, with no backend knowledge. Create it once after the engine exists
/// (it registers the <c>ui</c> technique and uploads the white texel + optional glyph atlas through the public API),
/// keep it for the engine's lifetime, and call <see cref="Draw"/> from a pass that blends the target. A painter is a
/// stable handle bundle, so a pass closure may capture it.
/// </summary>
public sealed class UIPainter
{
	private const string Technique = "ui";
	// Paper-white luminance reference for an HDR target: UIPaperWhiteNits / 80. SDR targets pass 1.0.
	private const float HdrPaperWhiteScale = 200f / 80f;

	private readonly UIHost _host;
	private readonly IEngine _engine;
	private readonly Texture _white;
	// The base UI textures a command selects by index: the 1×1 white texel (fills/borders) at 0, the optional MTSDF
	// glyph atlas at 1. Reused directly for image-free lists (no per-frame allocation); a list that references images
	// gets a wider array resolved in ResolveTextures.
	private readonly Texture[] _baseTextures;
	// Images uploaded once and cached by their stable ImageSource.TextureIndex (≥ 2).
	private readonly Dictionary<int, Texture> _imageTextures = [];

	private UIPainter(IEngine engine, UIHost host, Texture white, Texture? font)
	{
		_engine = engine;
		_host = host;
		_white = white;
		_baseTextures = font is { } f ? [white, f] : [white];
	}

	/// <summary>
	/// Register the <c>ui</c> technique and upload the UI textures, returning a painter to keep.
	/// <paramref name="fontAtlas"/> is the MTSDF glyph atlas the compositor laid glyphs out against;
	/// null sets up a painter for rect/border-only surfaces (no text).
	/// </summary>
	public static UIPainter Create(IEngine engine, UIHost host, FontAtlas? fontAtlas = null)
	{
		engine.DefineTechnique(Technique, new TechniqueDescriptor(Technique, HasVertexInput: true,
			Blend: BlendMode.PremultipliedAlpha, LinearFilter: true, Layout: VertexLayout.Overlay, HasListPush: true));

		var white = engine.CreateTexture(TextureData.FromRgba8(1, 1, [255, 255, 255, 255], TextureFormat.Rgba8Unorm, generateMips: false));

		Texture? font = null;
		if (fontAtlas is null)
			return new UIPainter(engine, host, white, font);

		var img = fontAtlas.Image;
		var td = TextureData.FromRgba8(img.Width, img.Height, img.Pixels.ToArray(), TextureFormat.Rgba8Unorm, generateMips: false);
		font = engine.CreateTexture(td);

		return new UIPainter(engine, host, white, font);
	}

	/// <summary>
	/// Submit the draw list registered under <paramref name="id"/> in this frame's composited output.
	/// A no-op when the frame carries no list for that id or it is empty. Call from a pass that blends the target.
	/// </summary>
	public void Draw(IRenderContext ctx, DrawListId id)
	{
		var lists = _host.DrawLists;
		DrawListData? draw = null;
		for (var i = 0; i < lists.Count; i++)
		{
			if (lists[i].Id != id)
				continue;
			draw = lists[i].Data;
			break;
		}

		if (draw is null || draw.Commands.IsEmpty)
			return;

		// SUIsei.Drawing.DrawCommand and Loom.Core.Hosting.DrawCommand are structurally identical (same fields,
		// same order, both [StructLayout(Sequential)]). MemoryMarshal.Cast reinterprets the bytes — zero copy.
		var loomCommands = MemoryMarshal.Cast<DrawCommand, LoomDrawCommand>(draw.Commands.Span);

		// The shared per-list push: surface-pixel → clip scale/translate, then the HDR paper-white lift.
		Span<float> push = stackalloc float[6];
		push[0] = 2.0f / draw.CoordinateSize.X;
		push[1] = 2.0f / draw.CoordinateSize.Y;
		push[2] = -1.0f;
		push[3] = -1.0f;
		push[4] = draw.IsHdrTarget ? HdrPaperWhiteScale : 1.0f;
		push[5] = BitConverter.UInt32BitsToSingle(draw.IsHdrTarget ? 1u : 0u);

		// SUIsei.Drawing.OverlayVertex has the same binary layout as Loom.Core.Geometry.OverlayVertex;
		// TVertex is inferred from draw.Vertices.Span — the backend sees raw bytes either way.
		ctx.DrawList(Technique, ResolveTextures(draw), draw.Vertices.Span, draw.Indices.Span, loomCommands, push);
	}

	// The texture array a frame binds, indexed by command TextureIndex. The image-free fast path reuses the shared
	// base array (white + optional glyph atlas); a frame that references images gets a wider array with each image
	// uploaded once (cached by index) and any gap filled with the white texel.
	private Texture[] ResolveTextures(DrawListData draw)
	{
		if (draw.Images.Count == 0)
			return _baseTextures;

		var max = _baseTextures.Length - 1;
		foreach (var image in draw.Images)
		{
			if (image.TextureIndex > max)
				max = image.TextureIndex;
		}

		var textures = new Texture[max + 1];
		for (var i = 0; i < textures.Length; i++)
			textures[i] = i < _baseTextures.Length ? _baseTextures[i] : _white;

		foreach (var image in draw.Images)
		{
			if (!_imageTextures.TryGetValue(image.TextureIndex, out var texture))
			{
				var atlas = image.Pixels;
				texture = _engine.CreateTexture(TextureData.FromRgba8(atlas.Width, atlas.Height, atlas.Pixels.ToArray(), TextureFormat.Rgba8Unorm, generateMips: false));
				_imageTextures[image.TextureIndex] = texture;
			}
			textures[image.TextureIndex] = texture;
		}
		return textures;
	}
}
