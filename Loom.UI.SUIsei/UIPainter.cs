using System.Runtime.InteropServices;
using Loom.Core.Geometry;
using Loom.Core.Graph;
using Loom.Core.Hosting;
using Loom.Core.Materials;
using SUIsei;
// Explicit aliases resolve the DrawCommand / DrawListData / DrawListId name conflicts between SUIsei.Drawing and
// Loom.Core.Hosting. Kept in alphabetical order: `dotnet format` sorts using directives, and a sort that has work to
// do lifts the first alias above this comment and detaches it.
using DrawCommand = SUIsei.Drawing.DrawCommand;
using DrawListData = SUIsei.Drawing.DrawListData;
using DrawListId = SUIsei.Drawing.DrawListId;
using LoomDrawCommand = Loom.Core.Hosting.DrawCommand;

namespace Loom.UI.SUIsei;

/// <summary>
/// Renders composited UI draw lists through the public engine API — the bridge between the headless
/// <see cref="UIHost"/> and the render graph, with no backend knowledge. Create it once after the engine exists
/// (it registers the <c>ui</c> technique and uploads the white texel + optional glyph atlas through the public API),
/// keep it for the engine's lifetime, and each frame call <see cref="Prepare"/> after <see cref="UIHost.Render"/> and
/// <see cref="Draw"/> from a pass that blends the target. A painter is a stable handle bundle, so a pass closure may
/// capture it.
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
	// glyph atlas at 1. Shared by every image-free list (no per-frame allocation); a list that references images gets a
	// wider array of its own.
	private readonly Texture[] _baseTextures;
	// Images uploaded once and cached by their stable ImageSource.TextureIndex (≥ 2).
	private readonly Dictionary<int, Texture> _imageTextures = [];
	// What Prepare resolved for each surface, kept across frames. Per surface because a painter can serve several, whose
	// revisions advance independently.
	private readonly Dictionary<DrawListId, PreparedSurface> _surfaces = [];

	/// <summary>
	/// One surface's drawing state, kept across frames: the draw-list stream its geometry is kept under, and the texture
	/// array its commands index — rebuilt only when the surface's list changes, so a UI that references images allocates
	/// nothing on a frame where it did not change.
	/// </summary>
	private sealed class PreparedSurface(int stream)
	{
		// Minted once by IEngine.AllocateDrawListStream, so no other producer on the engine can share it.
		public int Stream { get; } = stream;
		public Texture[] Textures = [];
		public int Count;
		public uint Revision = uint.MaxValue; // no real revision, so the first Prepare always builds
											  // Set by Prepare, consumed by Draw: a frame that skipped Prepare is caught at its first Draw rather than only
											  // once its UI happens to change.
		public bool Armed;
	}

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
	/// Make this frame's composited lists drawable: check each changed list's command kinds, upload the images it
	/// references, mint a draw-list stream for each surface seen for the first time, and rebuild a surface's texture
	/// array when its list changed. Call once per frame, after <see cref="UIHost.Render"/> and before
	/// <see cref="IEngine.Render"/>.
	/// <para>
	/// It is a step of its own because images cannot be uploaded where they are discovered. <see cref="Draw"/> runs in
	/// a pass callback, while the frame's commands are recorded, and an upload made there is submitted only ahead of
	/// the next frame — so a new image's first frame would sample a texture its pixels had not reached, which is why
	/// the engine refuses to create one there. A frame whose lists did not change does none of this, and allocates
	/// nothing.
	/// </para>
	/// </summary>
	public void Prepare()
	{
		var lists = _host.DrawLists;
		for (var i = 0; i < lists.Count; i++)
		{
			var (id, draw) = lists[i];
			if (!_surfaces.TryGetValue(id, out var surface))
				_surfaces[id] = surface = new PreparedSurface(_engine.AllocateDrawListStream());
			if (surface.Revision != draw.Revision)
			{
				ValidateKinds(id, draw.Commands.Span);
				ResolveTextures(surface, draw);
			}
			surface.Armed = true;
		}
	}

	/// <summary>
	/// Submit the draw list registered under <paramref name="id"/> in this frame's composited output, binding the
	/// textures <see cref="Prepare"/> resolved for it. A no-op when the frame carries no list for that id or it is
	/// empty. Call from a pass that blends the target. Throws when the list was not prepared this frame — when
	/// <see cref="Prepare"/> was skipped, ran before the host last rendered, or this surface was already drawn since.
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

		if (draw is null)
			return;

		if (!_surfaces.TryGetValue(id, out var surface) || !surface.Armed || surface.Revision != draw.Revision)
		{
			throw new InvalidOperationException(
				$"UIPainter.Draw: surface {id.Value}'s draw list was not prepared for this frame. Call UIPainter.Prepare() once " +
				"per frame, after UIHost.Render() and before IEngine.Render: it uploads the images the list references, which " +
				"cannot be uploaded from the pass that draws them. A surface is prepared for one Draw per frame.");
		}
		surface.Armed = false;

		if (draw.Commands.IsEmpty)
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
		// A UI surface is a stable geometry stream: the compositor advances Revision exactly when the composed list
		// changed, so an untouched surface re-submits the same identity and the backend can keep what it uploaded. The
		// stream is the surface's own engine-minted one, not its SUIsei surface id: every host numbers its surfaces from
		// zero, so two hosts' ids collide.
		var identity = new DrawListIdentity(surface.Stream, draw.Revision);
		ctx.DrawList(Technique, identity, surface.Textures.AsSpan(0, surface.Count), draw.Vertices.Span, draw.Indices.Span, loomCommands, push);
	}

	/// <summary>
	/// Refuse a list carrying a command kind the <c>ui</c> shader does not draw. A command's <c>Tag</c> selects the
	/// shader's branch: kinds 0–4 (fill, rounded rect, border, glyph, image) are drawn, and 5 and 6 are the
	/// group-opacity layer markers the draw-list walker consumes before a command reaches the shader. Anything else
	/// would reach the shader's <c>switch</c> and fall through to a plain fill, because a shader has no way to fail —
	/// so a compositor and shader that disagreed about a kind would draw the wrong thing without a word. Run once per
	/// revision, from <see cref="Prepare"/>, so a steady frame pays nothing.
	/// </summary>
	internal static void ValidateKinds(DrawListId surface, ReadOnlySpan<DrawCommand> commands)
	{
		for (var i = 0; i < commands.Length; i++)
		{
			var tag = commands[i].Tag;
			if (tag <= DrawListGeometry.KindImage || tag == DrawListGeometry.TagBeginLayer || tag == DrawListGeometry.TagEndLayer)
				continue;
			throw new InvalidOperationException(
				$"UIPainter: command {i} of surface {surface.Value}'s draw list has kind {tag}, which the ui shader does not draw. " +
				$"It draws kinds 0–{DrawListGeometry.KindImage} (fill, rounded rect, border, glyph, image), and " +
				$"{DrawListGeometry.TagBeginLayer} and {DrawListGeometry.TagEndLayer} are the group-opacity layer markers. " +
				"An unknown kind would draw as a plain fill, so the compositor that produced this list and ui.slang must " +
				"gain the kind together.");
		}
	}

	/// <summary>
	/// Build the texture array a surface's commands index by <c>TextureIndex</c>. An image-free list shares the base
	/// array (white + optional glyph atlas); a list that references images gets a wider array of its own, with each
	/// image uploaded once (cached by index) and any gap filled with the white texel.
	/// <para>
	/// Runs only when the list's <c>Revision</c> moves — the token the compositor advances exactly when the list
	/// changed — so a steady frame reuses what it built and a UI with images costs no allocation per frame.
	/// </para>
	/// </summary>
	private void ResolveTextures(PreparedSurface surface, DrawListData draw)
	{
		surface.Revision = draw.Revision;
		if (draw.Images.Count == 0)
		{
			surface.Textures = _baseTextures;
			surface.Count = _baseTextures.Length;
			return;
		}

		var max = _baseTextures.Length - 1;
		foreach (var image in draw.Images)
		{
			if (image.TextureIndex > max)
				max = image.TextureIndex;
		}

		// The base array is shared by every image-free surface, so a surface gains an array of its own before writing.
		var count = max + 1;
		if (surface.Textures == _baseTextures || surface.Textures.Length < count)
			surface.Textures = new Texture[Math.Max(count, surface.Textures.Length + (surface.Textures.Length / 2))];

		var textures = surface.Textures;
		for (var i = 0; i < count; i++)
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

		surface.Count = count;
	}
}
