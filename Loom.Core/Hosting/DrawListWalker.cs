using System.Runtime.CompilerServices;
using Loom.Core.Geometry;
using Loom.Core.Materials;
using Loom.Core.Primitives;

namespace Loom.Core.Hosting;

/// <summary>What a <see cref="DrawListWalker"/> step asks the backend to do.</summary>
public enum DrawListStep
{
	/// <summary>Draw <see cref="DrawListWalker.IndexCount"/> indices under the step's scissor and texture.</summary>
	Draw,

	/// <summary>Open a group-opacity layer at <see cref="DrawListWalker.Depth"/> and render into it.</summary>
	BeginLayer,

	/// <summary>Resolve the layer at <see cref="DrawListWalker.Depth"/> and composite it into its parent.</summary>
	EndLayer,
}

/// <summary>
/// Decodes a draw list's <see cref="DrawCommand"/> stream into the steps a backend records, so the state machine
/// around those steps exists once rather than per backend: the group-opacity layer nesting, the cap and its symmetric
/// suppression, the scissor, and the per-command texture memoisation.
/// <para>
/// A <c>ref struct</c> with a <c>MoveNext</c>/property shape rather than an enumerable — deliberately not usable
/// through an interface. The steady render loop must not allocate, and an interface enumerator boxes on every
/// submission.
/// </para>
/// <para>
/// <b>Layer identity is the depth.</b> The walker reports which nesting depth a step concerns and nothing else about
/// the target, because every backend's layer pool is already indexed by depth — so a backend looks its target up by
/// <see cref="Depth"/> instead of keeping a parallel stack of its own, and the tint and bounds a layer must remember
/// until its <see cref="DrawListStep.EndLayer"/> are held here.
/// </para>
/// </summary>
public ref struct DrawListWalker
{
	private readonly ReadOnlySpan<DrawCommand> _commands;
	private readonly ReadOnlySpan<Texture> _textures;
	private readonly ReadOnlySpan<OverlayVertex> _overlay;
	private readonly bool _layersSupported;
	private readonly int _targetWidth;
	private readonly int _targetHeight;
	private readonly float _surfaceWidth;
	private readonly float _surfaceHeight;
	private readonly float _scaleX;
	private readonly float _scaleY;

	private int _index;
	private int _openLayers;
	private int _suppressedLayers;
	private int _boundTextureId;
	private OpenLayerSlots _layers;

	// A layer's tint and bounds, remembered from its BeginLayer until its EndLayer needs them to composite.
	private struct OpenLayer
	{
		public Half4 Tint;
		public float X0, Y0, X1, Y1;
	}

	[InlineArray(DrawListGeometry.MaxLayerNesting)]
	private struct OpenLayerSlots
	{
		private OpenLayer _first;
	}

	/// <summary>Walk <paramref name="commands"/> against the target it is being recorded into.</summary>
	/// <param name="commands">The submission's command stream.</param>
	/// <param name="textures">The submission's bound textures; a command's index is clamped into this.</param>
	/// <param name="overlay">The vertices reinterpreted as <see cref="OverlayVertex"/>, for a layer marker's tint. May be empty when <paramref name="layersSupported"/> is false.</param>
	/// <param name="layersSupported">Whether this submission can carry group-opacity layers — the backend's judgement, because it depends on having a scope to suspend. When false the markers decode as ordinary (zero-index) draws.</param>
	/// <param name="targetWidth">Target width in pixels, for the scissor and the layer areas.</param>
	/// <param name="targetHeight">Target height in pixels.</param>
	/// <param name="surfaceWidth">Width of the list's own coordinate space, recovered from its push.</param>
	/// <param name="surfaceHeight">Height of the list's own coordinate space.</param>
	public DrawListWalker(
		ReadOnlySpan<DrawCommand> commands,
		ReadOnlySpan<Texture> textures,
		ReadOnlySpan<OverlayVertex> overlay,
		bool layersSupported,
		int targetWidth,
		int targetHeight,
		float surfaceWidth,
		float surfaceHeight)
	{
		_commands = commands;
		_textures = textures;
		_overlay = overlay;
		_layersSupported = layersSupported;
		_targetWidth = targetWidth;
		_targetHeight = targetHeight;
		_surfaceWidth = surfaceWidth;
		_surfaceHeight = surfaceHeight;
		_scaleX = surfaceWidth > 0f ? targetWidth / surfaceWidth : 1f;
		_scaleY = surfaceHeight > 0f ? targetHeight / surfaceHeight : 1f;
		_boundTextureId = -1;
	}

	/// <summary>What the current step is. Only meaningful after <see cref="MoveNext"/> returned true.</summary>
	public DrawListStep Step { get; private set; }

	/// <summary>The nesting depth a <see cref="DrawListStep.BeginLayer"/> opens, or an <see cref="DrawListStep.EndLayer"/> closes.</summary>
	public int Depth { get; private set; }

	/// <summary>A layer step's bounds in the list's coordinate space — the area worth rendering and compositing.</summary>
	public (float X0, float Y0, float X1, float Y1) Bounds { get; private set; }

	/// <summary>The same bounds in target pixels, for a backend that can narrow a layer's render or clear area.</summary>
	public (int X, int Y, int Width, int Height) LayerArea { get; private set; }

	/// <summary>A layer step's premultiplied group-alpha tint, to multiply the composite by.</summary>
	public Half4 Tint { get; private set; }

	/// <summary>A draw step's scissor in target pixels. Never empty — a fully clipped command is not reported at all.</summary>
	public (int X, int Y, int Width, int Height) Scissor { get; private set; }

	/// <summary>A draw step's first index within the submission's index stream.</summary>
	public int IndexOffset { get; private set; }

	/// <summary>A draw step's index count.</summary>
	public int IndexCount { get; private set; }

	/// <summary>A draw step's value added to every index before the vertex fetch.</summary>
	public int VertexOffset { get; private set; }

	/// <summary>A draw step's per-command tag, for the technique's push block.</summary>
	public uint Tag { get; private set; }

	/// <summary>The texture id a draw step samples, already resolved through the submission's texture span.</summary>
	public int TextureId { get; private set; }

	/// <summary>
	/// Whether this draw step needs its texture bound. False across a run of commands sharing one texture — a UI list
	/// is mostly the glyph atlas for run after run, so only a change costs a descriptor write. Forced true after a
	/// layer composite, which binds the layer as its own texture and so invalidates whatever was memoised.
	/// </summary>
	public bool TextureChanged { get; private set; }

	/// <summary>
	/// Layers still open once the walk ends. A well-formed list balances its markers and leaves this at zero; a backend
	/// unwinds whatever is left rather than returning with a layer's target still bound.
	/// </summary>
	public int OpenLayers => _openLayers;

	/// <summary>
	/// Whether the list nested layers past <see cref="DrawListGeometry.MaxLayerNesting"/>. The subtrees past the cap
	/// rendered into their parent unfaded; the backend reports it once through
	/// <see cref="DrawListGeometry.WarnLayerNestingExceeded"/>.
	/// </summary>
	public bool NestingExceeded { get; private set; }

	/// <summary>The nesting depth the list actually asked for, for that diagnostic.</summary>
	public int DeepestNesting { get; private set; }

	/// <summary>
	/// Advance to the next step, skipping what the backend has nothing to do for: a fully clipped draw, a layer marker
	/// past the nesting cap, and the <see cref="DrawListStep.EndLayer"/> matching one of those.
	/// </summary>
	public bool MoveNext()
	{
		while (_index < _commands.Length)
		{
			var c = _commands[_index++];

			if (_layersSupported && c.Tag == DrawListGeometry.TagBeginLayer)
			{
				if (_openLayers >= DrawListGeometry.MaxLayerNesting)
				{
					// Suppress the subtree symmetrically — see the EndLayer arm. Popping a real layer for this marker's
					// EndLayer would resolve an enclosing group early, and anything drawn after it would land in a
					// target already finished with.
					_suppressedLayers++;
					NestingExceeded = true;
					DeepestNesting = Math.Max(DeepestNesting, _openLayers + _suppressedLayers);
					continue;
				}

				Depth = _openLayers;
				Bounds = DrawListGeometry.LayerBounds(c.ClipRect, _surfaceWidth, _surfaceHeight);
				LayerArea = DrawListGeometry.LayerArea(Bounds, _scaleX, _scaleY, _targetWidth, _targetHeight);
				// The marker's first vertex carries the group's premultiplied tint; it is read, never drawn.
				Tint = (uint)c.VertexOffset < (uint)_overlay.Length ? _overlay[c.VertexOffset].Color : default;

				ref var opening = ref _layers[_openLayers];
				opening.Tint = Tint;
				(opening.X0, opening.Y0, opening.X1, opening.Y1) = Bounds;
				_openLayers++;
				DeepestNesting = Math.Max(DeepestNesting, _openLayers + _suppressedLayers);
				Step = DrawListStep.BeginLayer;
				return true;
			}

			if (_layersSupported && c.Tag == DrawListGeometry.TagEndLayer)
			{
				if (_suppressedLayers > 0)
				{
					_suppressedLayers--; // its BeginLayer opened no layer, so this closes none
					continue;
				}
				if (_openLayers == 0)
					continue; // an unmatched EndLayer: nothing to resolve

				_openLayers--;
				Depth = _openLayers;
				ref var closing = ref _layers[_openLayers];
				Tint = closing.Tint;
				Bounds = (closing.X0, closing.Y0, closing.X1, closing.Y1);
				LayerArea = DrawListGeometry.LayerArea(Bounds, _scaleX, _scaleY, _targetWidth, _targetHeight);
				// The composite binds the layer as its own texture, so whatever a draw had memoised is gone.
				_boundTextureId = -1;
				Step = DrawListStep.EndLayer;
				return true;
			}

			var scissor = DrawListGeometry.Scissor(c.ClipRect, _targetWidth, _targetHeight);
			if (scissor.Width <= 0 || scissor.Height <= 0)
				continue; // fully clipped

			Scissor = scissor;
			IndexOffset = c.IndexOffset;
			IndexCount = c.IndexCount;
			VertexOffset = c.VertexOffset;
			Tag = c.Tag;
			TextureId = _textures[Math.Clamp(c.TextureIndex, 0, _textures.Length - 1)].Id;
			TextureChanged = TextureId != _boundTextureId;
			_boundTextureId = TextureId;
			Step = DrawListStep.Draw;
			return true;
		}

		return false;
	}

	/// <summary>
	/// Pop one still-open layer for the defensive unwind, reporting the depth being abandoned; false once none are
	/// left. The layer is <em>not</em> composited: its <c>EndLayer</c> never arrived, so the list never asked for it,
	/// and all that has to happen is that the backend stops rendering into it.
	/// </summary>
	public bool TryUnwind(out int depth)
	{
		if (_openLayers == 0)
		{
			depth = 0;
			return false;
		}
		depth = --_openLayers;
		return true;
	}
}
