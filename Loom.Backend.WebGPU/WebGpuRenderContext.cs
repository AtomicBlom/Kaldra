using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;

using Loom.Core.Animation;
using Loom.Core.Geometry;
using Loom.Core.Graph;
using Loom.Core.Hosting;
using Loom.Core.Materials;

namespace Loom.Backend.WebGPU;

/// <summary>
/// The per-frame <see cref="IRenderContext"/> the WebGPU backend hands each pass closure. It records the
/// closure's draw/dispatch verbs into the frame's <see cref="WebGpuCommandWriter"/> (appended after the pass's
/// BeginRenderPass, before its EndPass) and stages their data into the <see cref="WebGpuUploadArena"/>. It
/// resolves a technique name to a render pipeline baked against the current pass's attachment formats (cached
/// per (name, formats), like the Vulkan backend), and builds a bind group from the pass's sampled inputs — cached
/// per (technique, resource handles) so, since transients are single-buffered and stable per graph, the steady
/// state creates no bind groups and the WASM↔JS boundary stays crossed once per frame. Pure: closures pull frame
/// inputs from here, never capture.
/// <para>
/// The draw verbs are filled in as the backend grows (fullscreen techniques, meshes, compute, draw lists);
/// until then they throw so an unported code path fails loudly rather than silently dropping work.
/// </para>
/// </summary>
[SupportedOSPlatform("browser")]
internal sealed partial class WebGpuRenderContext(WebGpuEngine engine) : IRenderContext, IDisposable
{
	private readonly WebGpuEngine _engine = engine;

	// Per-(name, attachment formats) baked pipelines — keyed by value so the per-draw lookup allocates nothing.
	// Misses bake a render pipeline from the technique's template; cleared/disposed on hot-reload.
	private readonly Dictionary<PipelineKey, WebGpuTechnique> _baked = [];
	// Per-(technique, bound resources) bind groups. Transients are stable per graph, so a fullscreen/draw's inputs
	// don't change frame to frame — the group is created once and reused. Cleared on graph swap (the view handles it
	// references are recycled) and hot-reload.
	private readonly Dictionary<BindGroupKey, int> _bindGroups = [];

	// The current pass's attachment formats (set by the engine before the pass's Record runs) — the formats a
	// technique bakes its pipeline against.
	private FormatIdBuffer _colorFormatIds;
	private int _colorCount;
	private int _depthFormatId = -1;
	private bool _depthReadOnly;

	public float DeltaTime { get; private set; }
	public float Time { get; private set; }
	public bool HdrOutput => false; // browser output is the SDR canvas; HDR canvas negotiation is a later concern

	public IReadOnlyList<(DrawListId Id, DrawListData Data)> DrawLists { get; private set; } = [];

	/// <summary>Set the per-frame inputs before the graph records.</summary>
	public void BeginFrame(float deltaTime, float time, IReadOnlyList<(DrawListId Id, DrawListData Data)> drawLists)
	{
		DeltaTime = deltaTime;
		Time = time;
		DrawLists = drawLists;
	}

	/// <summary>
	/// Set the current pass's attachment formats (its techniques bake against them) and whether depth is bound
	/// read-only — a read-only pass bakes with depth writes off so a transparent draw tests against, but does not
	/// disturb, the opaque depth. Called by the engine before the pass's Record runs.
	/// </summary>
	public void BeginPass(ReadOnlySpan<int> colorFormatIds, int depthFormatId, bool depthReadOnly)
	{
		if (colorFormatIds.Length > MaxColorFormats)
			throw new WebGpuException($"A pass has {colorFormatIds.Length} color attachments; the pipeline-key buffer holds {MaxColorFormats}.");
		_colorCount = colorFormatIds.Length;
		colorFormatIds.CopyTo(((Span<int>)_colorFormatIds)[.._colorCount]);
		_depthFormatId = depthFormatId;
		_depthReadOnly = depthReadOnly;
	}

	/// <summary>Resolve a technique to a render pipeline baked against the current pass's attachment formats.</summary>
	private WebGpuTechnique Bake(string technique)
	{
		var template = _engine.Template(technique);
		var colorFormats = ((ReadOnlySpan<int>)_colorFormatIds)[.._colorCount];
		var key = new PipelineKey(technique, colorFormats, _depthFormatId, _depthReadOnly);
		if (_baked.TryGetValue(key, out var cached))
			return cached;

		var baked = WebGpuTechnique.Bake(_engine.Handles, template, technique, colorFormats, _depthFormatId, depthWrite: !_depthReadOnly);
		_baked[key] = baked;
		return baked;
	}

	/// <summary>Drop the baked render pipelines (hot-reload); the templates are re-created by the engine before the next bake.</summary>
	public void ClearBaked()
	{
		foreach (var baked in _baked.Values)
			baked.Dispose();
		_baked.Clear();
		ClearBindGroups();
	}

	/// <summary>Release the cached bind groups — called on graph swap, when the transient views they bind are recycled.</summary>
	public void ClearBindGroups()
	{
		foreach (var handle in _bindGroups.Values)
			_engine.Handles.Release(WebGpuHandleAllocator.BindGroups, handle);
		_bindGroups.Clear();
	}

	public void Dispatch(string technique, (uint X, uint Y, uint Z) groups, ReadOnlySpan<float> push, ReadOnlySpan<ResourceHandle> sampled, ReadOnlySpan<ResourceHandle> storage) =>
		throw new WebGpuException($"Dispatch('{technique}') is not yet implemented on the WebGPU backend.");

	public void Draw(in DrawCall call) =>
		throw new WebGpuException($"Draw('{call.Technique}') is not yet implemented on the WebGPU backend.");

	public void DispatchSkin(string technique, Mesh skinnedMesh, ReadOnlySpan<Matrix4x4> palette, BufferHandle output, int vertexCount) =>
		throw new WebGpuException($"DispatchSkin('{technique}') is not yet implemented on the WebGPU backend.");

	public void DrawList<TVertex>(string technique, ReadOnlySpan<Texture> textures, ReadOnlySpan<TVertex> vertices, ReadOnlySpan<ushort> indices, ReadOnlySpan<DrawCommand> commands, ReadOnlySpan<float> push) where TVertex : unmanaged =>
		throw new WebGpuException($"DrawList('{technique}') is not yet implemented on the WebGPU backend.");

	public Texture? ResolveInspectable(int resourceId) => null;

	public void Dispose()
	{
		ClearBaked();
	}

	// Cap on a pass's color attachments — well above any realistic device maxColorAttachments (commonly 8).
	private const int MaxColorFormats = 16;

	[InlineArray(MaxColorFormats)]
	private struct FormatIdBuffer
	{
		private int _element0;
	}

	// Value-type, allocation-free pipeline-cache key: distinct attachment formats must map to distinct pipelines,
	// so Equals compares the full key rather than collapsing to a hash.
	private struct PipelineKey : IEquatable<PipelineKey>
	{
		private readonly string _technique;
		private FormatIdBuffer _colorFormats;
		private readonly int _colorCount;
		private readonly int _depthFormatId;
		private readonly bool _depthReadOnly;

		public PipelineKey(string technique, ReadOnlySpan<int> colorFormats, int depthFormatId, bool depthReadOnly)
		{
			_technique = technique;
			_colorCount = colorFormats.Length;
			for (var i = 0; i < colorFormats.Length; i++)
				_colorFormats[i] = colorFormats[i];
			_depthFormatId = depthFormatId;
			_depthReadOnly = depthReadOnly;
		}

		public bool Equals(PipelineKey other)
		{
			if (_colorCount != other._colorCount
				|| _depthFormatId != other._depthFormatId
				|| _depthReadOnly != other._depthReadOnly
				|| !string.Equals(_technique, other._technique, StringComparison.Ordinal))
				return false;
			for (var i = 0; i < _colorCount; i++)
				if (_colorFormats[i] != other._colorFormats[i])
					return false;
			return true;
		}

		public override bool Equals(object? obj) => obj is PipelineKey other && Equals(other);

		public override int GetHashCode()
		{
			var hash = new HashCode();
			hash.Add(_technique, StringComparer.Ordinal);
			hash.Add(_colorCount);
			for (var i = 0; i < _colorCount; i++)
				hash.Add(_colorFormats[i]);
			hash.Add(_depthFormatId);
			hash.Add(_depthReadOnly);
			return hash.ToHashCode();
		}
	}
}
