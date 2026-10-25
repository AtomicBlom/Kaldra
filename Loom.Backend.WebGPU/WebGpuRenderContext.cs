using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

using Loom.Core.Animation;
using Loom.Core.Geometry;
using Loom.Core.Graph;
using Loom.Core.Hosting;
using Loom.Core.Materials;
using Loom.Graph;

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
internal sealed partial class WebGpuRenderContext(WebGpuEngine engine, RecordValidator validation) : IRenderContext, IDisposable
{
	private readonly WebGpuEngine _engine = engine;
	private readonly RecordValidator _validate = validation;

	// Per-(name, attachment formats) baked pipelines — keyed by value so the per-draw lookup allocates nothing.
	// Misses bake a render pipeline from the technique's template; cleared/disposed on hot-reload.
	private readonly Dictionary<PipelineKey, WebGpuTechnique> _baked = [];
	// Compute pipelines have no attachment formats, so they key on the technique name alone.
	private readonly Dictionary<string, WebGpuTechnique> _computeBaked = [];
	// Per-(technique, bound resources) bind groups. Transients are stable per graph, so a fullscreen/draw's inputs
	// don't change frame to frame — the group is created once and reused. Cleared on graph swap and hot-reload, and
	// evicted entry by entry when the app releases a texture or mesh one names: every handle it names is recycled.
	private readonly BindingCache<int> _bindGroups = new();
	// Scratch for the groups one eviction removes, kept so a release allocates nothing once it has grown.
	private readonly List<int> _evictedGroups = [];

	// The current pass's attachment formats (set by the engine before the pass's Record runs) — the formats a
	// technique bakes its pipeline against.
	private FormatIdBuffer _colorFormatIds;
	private int _colorCount;
	private int _depthFormatId = -1;
	private bool _depthReadOnly;

	// The current pass's colour-attachment view handles + render area — captured so a draw list's group-opacity
	// layers can suspend this scope (end pass), render children offscreen, then resume it (LoadOp.Load) and composite.
	private FormatIdBuffer _colorViews;
	private int _scopeWidth;
	private int _scopeHeight;

	// The app-writable parameter/storage buffers the current pass .Reads (in .Reads order), bound to the technique's
	// uniform / storage-buffer slots at draw time.
	public const int MaxUniformBuffers = GraphLimits.MaxUniformBuffersPerPass;
	public const int MaxStorageBuffers = GraphLimits.MaxStorageBuffersPerPass;
	private readonly int[] _activeConstantsIds = new int[MaxUniformBuffers];
	private int _activeConstantsCount;
	private readonly int[] _activeStorageIds = new int[MaxStorageBuffers];
	private int _activeStorageCount;

	public double DeltaTime { get; private set; }
	public double Time { get; private set; }
	public bool HdrOutput => false; // browser output is the SDR canvas; HDR canvas negotiation is a later concern

	/// <summary>Set the per-frame inputs before the graph records.</summary>
	public void BeginFrame(double deltaTime, double time)
	{
		DeltaTime = deltaTime;
		Time = time;
	}

	/// <summary>
	/// Set the current pass's attachment formats (its techniques bake against them) and whether depth is bound
	/// read-only — a read-only pass bakes with depth writes off so a transparent draw tests against, but does not
	/// disturb, the opaque depth. Called by the engine before the pass's Record runs.
	/// </summary>
	public void BeginPass(GraphPass pass, ReadOnlySpan<int> colorFormatIds, int depthFormatId, bool depthReadOnly, ReadOnlySpan<int> colorViews, int areaWidth, int areaHeight, ReadOnlySpan<int> constantsIds = default, ReadOnlySpan<int> storageIds = default)
	{
		_validate.BeginPass(pass);
		if (colorFormatIds.Length > MaxColorFormats)
			throw new WebGpuException($"A pass has {colorFormatIds.Length} color attachments; the pipeline-key buffer holds {MaxColorFormats}.");
		_colorCount = colorFormatIds.Length;
		colorFormatIds.CopyTo(((Span<int>)_colorFormatIds)[.._colorCount]);
		colorViews.CopyTo(((Span<int>)_colorViews)[..Math.Min(colorViews.Length, MaxColorFormats)]);
		_scopeWidth = areaWidth;
		_scopeHeight = areaHeight;
		_depthFormatId = depthFormatId;
		_depthReadOnly = depthReadOnly;
		// Derivation rejects a pass that exceeds either cap, so these fit by construction. Copying the whole span
		// rather than clamping it means an engine bug would throw here instead of silently mis-binding every slot
		// past the cap, which is what the clamp used to do.
		_activeConstantsCount = constantsIds.Length;
		constantsIds.CopyTo(_activeConstantsIds);
		_activeStorageCount = storageIds.Length;
		storageIds.CopyTo(_activeStorageIds);
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

	/// <summary>Resolve a compute technique to its baked compute pipeline (keyed by name — no attachment formats).</summary>
	private WebGpuTechnique BakeCompute(string technique)
	{
		if (_computeBaked.TryGetValue(technique, out var cached))
			return cached;
		var baked = WebGpuTechnique.BakeCompute(_engine.Handles, _engine.Template(technique), technique);
		_computeBaked[technique] = baked;
		return baked;
	}

	/// <summary>Drop the baked render/compute pipelines (hot-reload); the templates are re-created by the engine before the next bake.</summary>
	public void ClearBaked()
	{
		foreach (var baked in _baked.Values)
			baked.Dispose();
		_baked.Clear();
		foreach (var baked in _computeBaked.Values)
			baked.Dispose();
		_computeBaked.Clear();
		ClearBindGroups();
	}

	/// <summary>Release the cached bind groups — called on graph swap, when the transient views they bind are recycled.</summary>
	public void ClearBindGroups()
	{
		foreach (var handle in _bindGroups.Values)
			_engine.Handles.Release(WebGpuHandleAllocator.BindGroups, handle);
		_bindGroups.Clear();
	}

	/// <summary>
	/// Pre-skin a mesh's bind-pose vertices by the bone palette into <paramref name="output"/> with a compute
	/// dispatch (one thread per vertex). Binds three storage buffers at slots 0,1,2 — the bind-pose vertices (the
	/// mesh's own long-lived buffer, which carries storage usage), the per-frame bone palette (staged into the storage
	/// ring), and the skinned output (the transient the pass ReadWrites) — and pushes the vertex count. The palette's
	/// ring offset is deterministic across frames (the ring resets each frame and this is its only user), so the bound
	/// bind group stays cache-stable while its contents refresh.
	/// </summary>
	public void DispatchSkin(string technique, Mesh skinnedMesh, ReadOnlySpan<Matrix4x4> palette, BufferHandle output, int vertexCount)
	{
		if (vertexCount <= 0 || palette.Length == 0)
			return;

		var baked = BakeCompute(technique);
		var bindings = baked.Template.Bindings;
		var writer = _engine.Writer;
		writer.SetComputePipeline(baked.Pipeline);

		var bindPose = _engine.Meshes.BindPoseBuffer(skinnedMesh.Id);
		var paletteBytes = MemoryMarshal.AsBytes(palette);
		var paletteOffset = _engine.StorageRing.Reserve(paletteBytes, paletteBytes.Length);
		var outputBuffer = _engine.Transients.BufferHandle(output.Id);

		Span<int> entries = stackalloc int[bindings.Entries.Count * 5];
		var e = 0;
		var storage = bindings.StorageBufferBindings;
		AddEntry(entries, ref e, storage[0], EntryBuffer, bindPose, 0, 0);
		AddEntry(entries, ref e, storage[1], EntryBuffer, _engine.StorageRing.Buffer, paletteOffset, paletteBytes.Length);
		AddEntry(entries, ref e, storage[2], EntryBuffer, outputBuffer, 0, 0);

		Span<int> dynOffsets = stackalloc int[1];
		var dynCount = 0;
		if (bindings.PushBinding >= 0)
		{
			Span<float> push = [vertexCount];
			Span<byte> pushBytes = stackalloc byte[bindings.PushByteSize];
			pushBytes.Clear();
			MemoryMarshal.AsBytes(push)[..Math.Min(sizeof(float), pushBytes.Length)].CopyTo(pushBytes);
			dynOffsets[dynCount++] = _engine.UniformRing.Reserve(pushBytes, bindings.PushByteSize);
			AddEntry(entries, ref e, bindings.PushBinding, EntryBuffer, _engine.UniformRing.Buffer, 0, bindings.PushByteSize);
		}

		var bindGroup = GetOrCreateBindGroup(baked.Template.BindGroupLayout, entries[..e]);
		writer.SetBindGroup(0, bindGroup, dynOffsets[..dynCount]);
		writer.Dispatch((vertexCount + 63) / 64, 1, 1);
	}

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
