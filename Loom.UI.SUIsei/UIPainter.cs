using System.Runtime.InteropServices;
using Loom.Core.Geometry;
using Loom.Core.Graph;
using Loom.Core.Hosting;
using Loom.Core.Materials;
using SUIsei;
using SUIsei.Drawing;
using SUIsei.Extensions;
// Explicit aliases resolve the DrawCommand / DrawListData / DrawListId name conflicts between SUIsei.Drawing and
// Loom.Core.Hosting. Kept in alphabetical order: `dotnet format` sorts using directives, and a sort that has work to
// do lifts the first alias above this comment and detaches it.
using DrawCommand = SUIsei.Drawing.DrawCommand;
using DrawListData = SUIsei.Drawing.DrawListData;
using DrawListId = SUIsei.Drawing.DrawListId;
using LoomDrawCommand = Loom.Core.Hosting.DrawCommand;
using TextureSource = SUIsei.Drawing.TextureSource;

namespace Loom.UI.SUIsei;

/// <summary>
/// Renders composited UI draw lists through the public engine API — the bridge between the headless
/// <see cref="UIHost"/> and the render graph, with no backend knowledge. Create it once after the engine exists (it
/// registers the <c>ui</c> technique), keep it for the engine's lifetime, and each frame call <see cref="Prepare"/>
/// after <see cref="UIHost.Render"/> and <see cref="Draw"/> from a pass that blends the target. A painter is a stable
/// handle bundle, so a pass closure may capture it.
/// <para>
/// Every texture a list samples arrives in the list's own slot table (<see cref="DrawListData.TextureSlots"/>) — the
/// white texel, the compositor's glyph-atlas pages, images — so the painter uploads nothing up front and needs no
/// font: it uploads each source the first time any surface names it, shares it between every surface that does, and
/// releases it when the host reports that none does (<see cref="UIHost.ReleasedTextures"/>). A
/// <see cref="HostImageSource"/> is the exception: its texture is the app's, bound by tag through
/// <see cref="SetExternalTexture"/>, and never uploaded or released here.
/// </para>
/// </summary>
public sealed class UIPainter
{
	private const string Technique = "ui";
	// The vector kind: ui.slang's KIND_VECTOR, the one drawn kind numbered past the layer markers.
	private const uint KindVector = (uint)UIPrimKind.Vector;
	// ui.slang's STRAIGHT_ALPHA: set on an image or vector command whose slot holds straight-alpha texels, so the shader
	// premultiplies the filtered sample. Above the kind's byte, which SUIsei's kinds never reach.
	private const uint TagStraightAlpha = 0x100;
	// Paper-white luminance reference for an HDR target: UIPaperWhiteNits / 80. SDR targets pass 1.0.
	private const float HdrPaperWhiteScale = 200f / 80f;

	private readonly UIHost _host;
	private readonly IEngine _engine;
	// Every uploaded source, once however many surfaces share it. Keyed by the source itself: sources are identities,
	// compared by reference.
	private readonly Dictionary<TextureSource, Resident> _resident = new(ReferenceEqualityComparer.Instance);
	// The app's textures for host images, by tag.
	private readonly Dictionary<string, ResourceHandle> _external = new(StringComparer.Ordinal);
	// Moves whenever a tag is bound to a different texture, so a surface showing a host image rebinds it.
	private uint _externalVersion;
	// What Prepare resolved for each surface, kept across frames. Per surface because a painter can serve several, whose
	// lists change independently.
	private readonly Dictionary<DrawListId, PreparedSurface> _surfaces = [];
	// Counts Prepare calls, so a surface that a pass did not see — its root was removed — can be told apart and swept.
	private uint _pass;

	/// <summary>An uploaded source: its texture and the content version that texture holds.</summary>
	private sealed class Resident(Texture texture, uint version)
	{
		public Texture Texture = texture;
		public uint Version = version;
	}

	/// <summary>
	/// One surface's drawing state, kept across frames: the draw-list stream its geometry is kept under, the texture
	/// array its commands index by slot, and its commands with each one's alpha flag folded into its tag. Both are rebuilt
	/// only when the list or its textures change, so a frame whose UI did not change allocates nothing.
	/// </summary>
	private sealed class PreparedSurface(int stream)
	{
		// Minted once by IEngine.AllocateDrawListStream, so no other producer on the engine can share it.
		public int Stream { get; } = stream;
		public TextureRef[] Textures = [];
		public int Count;
		public LoomDrawCommand[] Commands = [];
		public int CommandCount;
		// Whether a slot names a host image, and the external binding it was resolved against.
		public bool ShowsHostImage;
		public uint ExternalVersion;
		// The revisions last checked and resolved; MaxValue, which no list carries, means not yet.
		public uint Revision = uint.MaxValue;
		public uint TextureRevision = uint.MaxValue;
		public uint SeenIn;
		// Set by Prepare, consumed by Draw: a frame that skipped Prepare is caught at its first Draw rather than only
		// once its UI happens to change.
		public bool Armed;
	}

	private UIPainter(IEngine engine, UIHost host)
	{
		_engine = engine;
		_host = host;
	}

	/// <summary>Register the <c>ui</c> technique and return a painter to keep. Nothing is uploaded until <see cref="Prepare"/>.</summary>
	public static UIPainter Create(IEngine engine, UIHost host)
	{
		engine.DefineTechnique(Technique, new TechniqueDescriptor(Technique, HasVertexInput: true,
			Blend: BlendMode.PremultipliedAlpha, LinearFilter: true, Layout: VertexLayout.Overlay, HasListPush: true));
		return new UIPainter(engine, host);
	}

	/// <summary>
	/// Bind <paramref name="texture"/>, a graph texture the app renders, to every <see cref="HostImageSource"/> tagged
	/// <paramref name="tag"/> — a minimap, a render-to-texture preview, a video frame the app draws itself. Binding a tag
	/// again replaces its texture: call it with the new handle whenever the graph is rebuilt.
	/// <para>
	/// The pass that draws the UI must declare the texture with <c>.Samples</c>, as for any sampled input; that is what
	/// orders the UI after the pass that renders it. The texture is read as the host image declares: its
	/// <see cref="TextureSource.ColorSpace"/> must match the target's format (an sRGB image needs an sRGB format, whose
	/// sampler decodes it), and a <see cref="TextureAlphaMode.Straight"/> one is premultiplied as it is sampled. A host
	/// image whose tag nothing was bound to fails <see cref="Prepare"/>.
	/// </para>
	/// </summary>
	/// <exception cref="ArgumentException">The tag is empty, the handle is not a graph texture, or its format cannot be sampled by a draw list.</exception>
	public void SetExternalTexture(string tag, ITextureHandle texture)
	{
		ArgumentException.ThrowIfNullOrEmpty(tag);
		var resource = new TextureRef(texture).Resource!;
		if (!resource.Format.IsFilterableFloat)
		{
			throw new ArgumentException(
				$"UIPainter.SetExternalTexture: '{resource.Name}' is {resource.Format}, which a draw list cannot sample on every " +
				"backend (it reads its textures as filterable floats). Bind a colour target.", nameof(texture));
		}
		if (_external.TryGetValue(tag, out var bound) && ReferenceEquals(bound, resource))
			return;
		_external[tag] = resource;
		_externalVersion++;
	}

	/// <summary>
	/// Make this frame's composited lists drawable. For each list that changed: check its commands, upload the sources
	/// its slot table newly names (and replace one whose texels moved), bind host images to their textures, and rebuild
	/// its texture array and command tags. Then release the textures of the sources the host's last render let go of
	/// (<see cref="UIHost.ReleasedTextures"/>: no surface draws them any more, or they were disposed) — the engine keeps
	/// each until the frames in flight are done with it. A surface seen for the first time mints a draw-list stream; one
	/// whose root is gone is forgotten. Call once per frame, after <see cref="UIHost.Render"/> and before
	/// <see cref="IEngine.Render"/>.
	/// <para>
	/// It is a step of its own because textures cannot be created or released where they are discovered.
	/// <see cref="Draw"/> runs in a pass callback, while the frame's commands are recorded: an upload made there is
	/// submitted only ahead of the next frame, so a new image's first frame would sample a texture its pixels had not
	/// reached, and the engine refuses it. A frame whose lists did not change does none of this, and allocates nothing.
	/// </para>
	/// </summary>
	/// <exception cref="InvalidOperationException">A list samples a host image whose tag no texture is bound to, or whose colour space its texture's format does not decode; or a source the same render reported released.</exception>
	public void Prepare()
	{
		_pass++;
		var lists = _host.DrawLists;
		for (var i = 0; i < lists.Count; i++)
		{
			var (id, draw) = lists[i];
			if (!_surfaces.TryGetValue(id, out var surface))
				_surfaces[id] = surface = new PreparedSurface(_engine.AllocateDrawListStream());
			surface.SeenIn = _pass;

			// The texture revision is checked every frame, not only when the geometry changes: a source's content can
			// change under a list whose geometry did not. A host image's binding can change under both.
			var revisionMoved = surface.Revision != draw.Revision;
			var texturesMoved = surface.TextureRevision != draw.TextureRevision
				|| (surface.ShowsHostImage && surface.ExternalVersion != _externalVersion);
			if (revisionMoved || texturesMoved)
			{
				var slots = draw.TextureSlots;
				ValidateKinds(id, draw.Commands.Span);
				ValidateSlots(id, draw.Commands.Span, slots.Count);
				if (texturesMoved)
					Resolve(id, surface, slots);
				Retag(surface, draw.Commands.Span, slots);
				surface.Revision = draw.Revision;
				surface.TextureRevision = draw.TextureRevision;
			}
			surface.Armed = true;
		}

		// What the host's last render let go of: nothing about to be drawn samples these, which is the host's promise and
		// is checked before anything is released on the strength of it. A host image's texture is the app's and was never
		// uploaded, so it has nothing here to release. Reading the list again after a second Prepare in the same frame
		// finds nothing left to release.
		var released = _host.ReleasedTextures;
		if (released.Count > 0)
			RequireUnnamed(released, lists);
		for (var i = 0; i < released.Count; i++)
		{
			if (_resident.Remove(released[i], out var resident))
				_engine.ReleaseTexture(resident.Texture);
		}

		// A surface the host no longer lists (its root was removed) is forgotten; the host reports its sources released.
		// Removing the entry being enumerated does not invalidate a Dictionary enumerator, so this sweep allocates nothing.
		foreach (var (id, surface) in _surfaces)
		{
			if (surface.SeenIn != _pass)
				_surfaces.Remove(id);
		}
	}

	/// <summary>
	/// Submit the draw list registered under <paramref name="id"/> in this frame's composited output, binding the
	/// textures <see cref="Prepare"/> resolved for it. A no-op when the frame carries no list for that id or it is
	/// empty. Call from a pass that blends the target, and that declares with <c>.Samples</c> every texture bound by
	/// <see cref="SetExternalTexture"/> the list shows. Throws when the list was not prepared this frame — when
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

		if (!_surfaces.TryGetValue(id, out var surface) || !surface.Armed
			|| surface.Revision != draw.Revision || surface.TextureRevision != draw.TextureRevision)
		{
			throw new InvalidOperationException(
				$"UIPainter.Draw: surface {id.Value}'s draw list was not prepared for this frame. Call UIPainter.Prepare() once " +
				"per frame, after UIHost.Render() and before IEngine.Render: it uploads the textures the list samples, which " +
				"cannot be uploaded from the pass that draws them. A surface is prepared for one Draw per frame.");
		}
		surface.Armed = false;

		if (surface.CommandCount == 0)
			return;

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
		ctx.DrawList(Technique, identity, surface.Textures.AsSpan(0, surface.Count), draw.Vertices.Span, draw.Indices.Span,
			surface.Commands.AsSpan(0, surface.CommandCount), push);
	}

	/// <summary>
	/// Refuse a list carrying a command kind the <c>ui</c> shader does not draw. A command's <c>Tag</c> selects the
	/// shader's branch: kinds 0–4 (fill, rounded rect, border, glyph, image) and 7 (vector) are drawn, and 5 and 6 are
	/// the group-opacity layer markers the draw-list walker consumes before a command reaches the shader. Anything else
	/// would reach the shader's <c>switch</c> and fall through to a plain fill, because a shader has no way to fail —
	/// so a compositor and shader that disagreed about a kind would draw the wrong thing without a word. Run once per
	/// revision, from <see cref="Prepare"/>, so a steady frame pays nothing.
	/// </summary>
	internal static void ValidateKinds(DrawListId surface, ReadOnlySpan<DrawCommand> commands)
	{
		for (var i = 0; i < commands.Length; i++)
		{
			var tag = commands[i].Tag;
			if (tag <= DrawListGeometry.KindImage || tag == KindVector
				|| tag == DrawListGeometry.TagBeginLayer || tag == DrawListGeometry.TagEndLayer)
			{
				continue;
			}
			throw new InvalidOperationException(
				$"UIPainter: command {i} of surface {surface.Value}'s draw list has kind {tag}, which the ui shader does not draw. " +
				$"It draws kinds 0–{DrawListGeometry.KindImage} (fill, rounded rect, border, glyph, image) and {KindVector} (vector), " +
				$"and {DrawListGeometry.TagBeginLayer} and {DrawListGeometry.TagEndLayer} are the group-opacity layer markers. " +
				"An unknown kind would draw as a plain fill, so the compositor that produced this list and ui.slang must " +
				"gain the kind together.");
		}
	}

	/// <summary>
	/// Refuse a list whose command names a texture slot its slot table does not have. The backend would index the
	/// texture array out of range mid-recording; this names the command and the surface instead, once per revision.
	/// </summary>
	internal static void ValidateSlots(DrawListId surface, ReadOnlySpan<DrawCommand> commands, int slotCount)
	{
		for (var i = 0; i < commands.Length; i++)
		{
			var slot = commands[i].TextureIndex;
			if (slot >= 0 && slot < slotCount)
				continue;
			throw new InvalidOperationException(
				$"UIPainter: command {i} of surface {surface.Value}'s draw list samples texture slot {slot}, but the list's " +
				$"slot table has {slotCount} slot(s). A command's TextureIndex indexes its own list's TextureSlots.");
		}
	}

	/// <summary>
	/// Refuse to release a source a list about to be drawn still names. The host promises that no list of a render names a
	/// source that render reported released, and SUIsei's own compositor keeps it; one that broke it would have the painter
	/// release a texture the frame then samples. Runs only on a frame that released something, so a steady frame pays
	/// nothing.
	/// </summary>
	private static void RequireUnnamed(IReadOnlyList<TextureSource> released, IReadOnlyList<(DrawListId Id, DrawListData Data)> lists)
	{
		for (var l = 0; l < lists.Count; l++)
		{
			var slots = lists[l].Data.TextureSlots;
			for (var s = 0; s < slots.Count; s++)
			{
				for (var r = 0; r < released.Count; r++)
				{
					if (!ReferenceEquals(slots[s], released[r]))
						continue;
					throw new InvalidOperationException(
						$"UIPainter: surface {lists[l].Id.Value}'s draw list samples a {slots[s].GetType().Name} that the same render " +
						"reported released (UIHost.ReleasedTextures). No list of a render may name a source it released; the " +
						"compositor that drew this surface broke that, and releasing the texture now would free one this frame samples.");
				}
			}
		}
	}

	/// <summary>Rebuild a surface's texture array, and which of its slots are straight alpha, from its slot table.</summary>
	private void Resolve(DrawListId id, PreparedSurface surface, IReadOnlyList<TextureSource> slots)
	{
		var count = slots.Count;
		if (surface.Textures.Length < count)
		{
			surface.Textures = new TextureRef[Math.Max(count, surface.Textures.Length * 2)];
		}

		surface.ShowsHostImage = false;
		for (var s = 0; s < count; s++)
		{
			var source = slots[s];
			surface.Textures[s] = source is HostImageSource host ? Bind(id, host) : Upload(source);
			surface.ShowsHostImage |= source is HostImageSource;
		}
		surface.Count = count;
		surface.ExternalVersion = _externalVersion;
	}

	/// <summary>
	/// Copy a list's commands into the surface's own array through <see cref="CopyCommands"/>; the vertices and indices
	/// still reach the backend uncopied.
	/// </summary>
	private static void Retag(PreparedSurface surface, ReadOnlySpan<DrawCommand> commands, IReadOnlyList<TextureSource> slots)
	{
		if (surface.Commands.Length < commands.Length)
			surface.Commands = new LoomDrawCommand[Math.Max(commands.Length, surface.Commands.Length * 2)];

		CopyCommands(commands, slots, surface.Commands);
		surface.CommandCount = commands.Length;
	}

	/// <summary>
	/// Copy <paramref name="commands"/> into <paramref name="destination"/> as the engine's commands, folding the alpha mode
	/// of each image and vector command's slot in <paramref name="slots"/> into its tag (<see cref="TagStraightAlpha"/>).
	/// SUIsei declares alpha per source, not per command, and the shader's only per-command input is the tag. Anything that
	/// submits a SUIsei list through the <c>ui</c> technique hands it commands made this way.
	/// </summary>
	internal static void CopyCommands(ReadOnlySpan<DrawCommand> commands, IReadOnlyList<TextureSource> slots, Span<LoomDrawCommand> destination)
	{
		// SUIsei.Drawing.DrawCommand and Loom.Core.Hosting.DrawCommand are structurally identical (same fields, same
		// order, both [StructLayout(Sequential)]), so the copy is a reinterpretation of the bytes.
		MemoryMarshal.Cast<DrawCommand, LoomDrawCommand>(commands).CopyTo(destination);
		for (var i = 0; i < commands.Length; i++)
		{
			ref var command = ref destination[i];
			if ((command.Tag == DrawListGeometry.KindImage || command.Tag == KindVector)
				&& slots[command.TextureIndex].AlphaMode == TextureAlphaMode.Straight)
				command = command with { Tag = command.Tag | TagStraightAlpha };
		}
	}

	/// <summary>
	/// The texture for <paramref name="source"/>, uploading it the first time any surface names it. A source whose content
	/// <see cref="TextureSource.Version"/> moved since its upload gets its new texels uploaded as a new texture and the old
	/// one released: every other surface naming it sees its texture revision move as well, so all of them rebind in the
	/// same <see cref="Prepare"/>.
	/// <para>
	/// A disposed source is drawn like any other. A list composited before the app disposed it still names it, and the
	/// host reports it released only at its next render, which redraws those surfaces without it — so the frame between
	/// shows it as it was, and its texels are still there to upload.
	/// </para>
	/// </summary>
	private Texture Upload(TextureSource source)
	{
		if (!_resident.TryGetValue(source, out var resident))
		{
			_resident[source] = resident = new Resident(CreateTexture(source), source.Version);
		}
		else if (resident.Version != source.Version)
		{
			var stale = resident.Texture;
			resident.Texture = CreateTexture(source);
			resident.Version = source.Version;
			_engine.ReleaseTexture(stale);
		}
		return resident.Texture;
	}

	/// <summary>The graph texture bound to <paramref name="host"/>'s tag, checked against how the host image reads.</summary>
	private TextureRef Bind(DrawListId id, HostImageSource host)
	{
		if (!_external.TryGetValue(host.Tag, out var texture))
		{
			throw new InvalidOperationException(
				$"UIPainter: surface {id.Value}'s draw list shows host image \"{host.Tag}\", but no texture is bound to that tag. " +
				"Call UIPainter.SetExternalTexture(tag, handle) with the graph texture it should show.");
		}

		var srgb = texture.Format is Format.Rgba8Srgb or Format.Bgra8Srgb;
		if (srgb != (host.ColorSpace == TextureColorSpace.Srgb))
		{
			throw new InvalidOperationException(
				$"UIPainter: host image \"{host.Tag}\" declares {host.ColorSpace} texels, but its texture '{texture.Name}' is " +
				$"{texture.Format}, whose sampler {(srgb ? "decodes sRGB" : "reads the bytes as they are")}. Declare the colour " +
				"space the target holds, or render into a target whose format decodes it.");
		}
		return new TextureRef((ITextureHandle)texture);
	}

	/// <summary>Upload <paramref name="source"/>'s texels as <see cref="UploadData"/> describes them.</summary>
	private Texture CreateTexture(TextureSource source) => _engine.CreateTexture(UploadData(source));

	/// <summary>
	/// The texels <paramref name="source"/> uploads as. The format is how the GPU decodes them: an sRGB source's sampler
	/// decodes each texel to linear light before filtering, exactly as SUIsei's rasterizer does; a linear one (an atlas
	/// page's distances, the white texel) is read raw. Alpha mode is not the format's business: the shader premultiplies a
	/// straight slot's sample, told so by <see cref="CopyCommands"/>.
	/// </summary>
	internal static TextureData UploadData(TextureSource source) =>
		TextureData.FromRgba8(source.Width, source.Height, source.Pixels.Span,
			source.ColorSpace == TextureColorSpace.Srgb ? TextureFormat.Rgba8Srgb : TextureFormat.Rgba8Unorm, generateMips: false);

	// What the painter holds, for tests that pin its sharing and release rules without a capture.
	internal int ResidentSourceCount => _resident.Count;

	internal Texture? ResidentTexture(TextureSource source) => _resident.TryGetValue(source, out var resident) ? resident.Texture : null;

	internal ReadOnlySpan<TextureRef> SlotTextures(DrawListId id) =>
		_surfaces.TryGetValue(id, out var surface) ? surface.Textures.AsSpan(0, surface.Count) : default;

	internal ReadOnlySpan<LoomDrawCommand> PreparedCommands(DrawListId id) =>
		_surfaces.TryGetValue(id, out var surface) ? surface.Commands.AsSpan(0, surface.CommandCount) : default;
}
