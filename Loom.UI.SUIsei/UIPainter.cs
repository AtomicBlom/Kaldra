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
/// releases it once none does.
/// </para>
/// </summary>
public sealed class UIPainter
{
	private const string Technique = "ui";
	// The vector kind: ui.slang's KIND_VECTOR, the one drawn kind numbered past the layer markers.
	private const uint KindVector = 7;
	// Paper-white luminance reference for an HDR target: UIPaperWhiteNits / 80. SDR targets pass 1.0.
	private const float HdrPaperWhiteScale = 200f / 80f;

	private readonly UIHost _host;
	private readonly IEngine _engine;
	// Every source some surface's slot table names, uploaded once however many surfaces share it. Keyed by the source
	// itself: sources are identities, compared by reference.
	private readonly Dictionary<TextureSource, Resident> _resident = new(ReferenceEqualityComparer.Instance);
	// What Prepare resolved for each surface, kept across frames. Per surface because a painter can serve several, whose
	// lists change independently.
	private readonly Dictionary<DrawListId, PreparedSurface> _surfaces = [];
	// Counts Prepare calls, so a surface that a pass did not see — its root was removed — can be told apart and swept.
	private uint _pass;

	/// <summary>An uploaded source: its texture, the content version that texture holds, and how many surfaces name it.</summary>
	private sealed class Resident(Texture texture, uint version)
	{
		public Texture Texture = texture;
		public uint Version = version;
		public int Surfaces;
	}

	/// <summary>
	/// One surface's drawing state, kept across frames: the draw-list stream its geometry is kept under, the texture
	/// array its commands index by slot, and the sources it holds references on. The array is rebuilt only when the
	/// list's <see cref="DrawListData.TextureRevision"/> moves, so a frame whose UI did not change allocates nothing.
	/// </summary>
	private sealed class PreparedSurface(int stream)
	{
		// Minted once by IEngine.AllocateDrawListStream, so no other producer on the engine can share it.
		public int Stream { get; } = stream;
		public Texture[] Textures = [];
		public int Count;
		// The sources this surface holds a reference on (its slot table, as last resolved), and a second array to build
		// the next table into while the old references are still held.
		public TextureSource[] Sources = [];
		public TextureSource[] Building = [];
		public int SourceCount;
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
	/// Make this frame's composited lists drawable. For each list that changed: check its commands, upload the sources
	/// its slot table newly names, rebuild its texture array, and let go of the sources it stopped naming — a source no
	/// surface names any more is released, and the engine keeps it until the frames in flight are done with it. A
	/// surface seen for the first time mints a draw-list stream; one whose root is gone gives up its sources. Call once
	/// per frame, after <see cref="UIHost.Render"/> and before <see cref="IEngine.Render"/>.
	/// <para>
	/// It is a step of its own because textures cannot be created or released where they are discovered.
	/// <see cref="Draw"/> runs in a pass callback, while the frame's commands are recorded: an upload made there is
	/// submitted only ahead of the next frame, so a new image's first frame would sample a texture its pixels had not
	/// reached, and the engine refuses it. A frame whose lists did not change does none of this, and allocates nothing.
	/// </para>
	/// </summary>
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
			// change under a list whose geometry did not.
			var revisionMoved = surface.Revision != draw.Revision;
			var texturesMoved = surface.TextureRevision != draw.TextureRevision;
			if (revisionMoved || texturesMoved)
			{
				var slots = draw.TextureSlots;
				ValidateKinds(id, draw.Commands.Span);
				ValidateSlots(id, draw.Commands.Span, slots.Count);
				if (texturesMoved)
					Resolve(surface, slots);
				surface.Revision = draw.Revision;
				surface.TextureRevision = draw.TextureRevision;
			}
			surface.Armed = true;
		}

		// A surface the host no longer lists (its root was removed) holds its sources no longer. Removing the entry being
		// enumerated does not invalidate a Dictionary enumerator, so this sweep allocates nothing.
		foreach (var (id, surface) in _surfaces)
		{
			if (surface.SeenIn == _pass)
				continue;
			for (var s = 0; s < surface.SourceCount; s++)
				Unreference(surface.Sources[s]);
			_surfaces.Remove(id);
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

		if (!_surfaces.TryGetValue(id, out var surface) || !surface.Armed
			|| surface.Revision != draw.Revision || surface.TextureRevision != draw.TextureRevision)
		{
			throw new InvalidOperationException(
				$"UIPainter.Draw: surface {id.Value}'s draw list was not prepared for this frame. Call UIPainter.Prepare() once " +
				"per frame, after UIHost.Render() and before IEngine.Render: it uploads the textures the list samples, which " +
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
	/// Rebuild a surface's texture array from its slot table. The new table's sources are referenced before the old
	/// table's are let go, so a source the surface keeps naming is never released and uploaded again in between.
	/// </summary>
	private void Resolve(PreparedSurface surface, IReadOnlyList<TextureSource> slots)
	{
		var count = slots.Count;
		if (surface.Textures.Length < count)
			surface.Textures = new Texture[Math.Max(count, surface.Textures.Length * 2)];
		if (surface.Building.Length < count)
			surface.Building = new TextureSource[Math.Max(count, surface.Building.Length * 2)];

		for (var s = 0; s < count; s++)
		{
			var source = slots[s];
			surface.Textures[s] = Reference(source);
			surface.Building[s] = source;
		}
		for (var s = 0; s < surface.SourceCount; s++)
			Unreference(surface.Sources[s]);

		(surface.Sources, surface.Building) = (surface.Building, surface.Sources);
		surface.SourceCount = count;
		surface.Count = count;
	}

	/// <summary>
	/// Take a reference on <paramref name="source"/> for one surface and return its texture, uploading it the first time
	/// any surface names it. A source whose content <see cref="TextureSource.Version"/> moved since its upload gets its
	/// new pixels uploaded as a new texture and the old one released: every other surface naming it sees its texture
	/// revision move as well, so all of them rebind in the same <see cref="Prepare"/>.
	/// </summary>
	private Texture Reference(TextureSource source)
	{
		if (!_resident.TryGetValue(source, out var resident))
		{
			_resident[source] = resident = new Resident(Upload(source), source.Version);
		}
		else if (resident.Version != source.Version)
		{
			var stale = resident.Texture;
			resident.Texture = Upload(source);
			resident.Version = source.Version;
			_engine.ReleaseTexture(stale);
		}
		resident.Surfaces++;
		return resident.Texture;
	}

	/// <summary>Let go of one surface's reference on <paramref name="source"/>, releasing its texture when it was the last.</summary>
	private void Unreference(TextureSource source)
	{
		var resident = _resident[source];
		if (--resident.Surfaces > 0)
			return;
		_resident.Remove(source);
		_engine.ReleaseTexture(resident.Texture);
	}

	private Texture Upload(TextureSource source) =>
		_engine.CreateTexture(TextureData.FromRgba8(source.Width, source.Height, source.Pixels.Span, TextureFormat.Rgba8Unorm, generateMips: false));

	// What the painter holds, for tests that pin its sharing and release rules without a capture.
	internal int ResidentSourceCount => _resident.Count;

	internal Texture? ResidentTexture(TextureSource source) => _resident.TryGetValue(source, out var resident) ? resident.Texture : null;

	internal ReadOnlySpan<Texture> SlotTextures(DrawListId id) =>
		_surfaces.TryGetValue(id, out var surface) ? surface.Textures.AsSpan(0, surface.Count) : default;
}
