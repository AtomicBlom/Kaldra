using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;

using Loom.Core;
using Loom.Core.Geometry;
using Loom.Core.Graph;
using Loom.Core.Hosting;
using Loom.Core.Materials;
using Loom.Core.Primitives;
using Loom.Graph;
using Silk.NET.Vulkan;
using VkFormat = Silk.NET.Vulkan.Format;

namespace Loom.Vulkan;

/// <summary>
/// The backend's <see cref="IRenderContext"/> — what a pass's <c>.Draw</c> callback issues GPU work
/// through. It is a thin recorder over the frame's command buffer: it resolves a technique name to a baked
/// pipeline (baked against the current pass's target format), wires up any sampled input as a descriptor
/// set, and emits the draw. State that changes per frame/pass (the command buffer, the target format) is
/// pushed in by the <see cref="VulkanEngine"/> via <see cref="BeginFrame"/>/<see cref="BeginPass"/> so the
/// callbacks stay the pure, capture-free closures the design wants.
/// </summary>
internal sealed unsafe partial class VulkanRenderContext : IRenderContext, IDisposable
{
	private readonly VulkanContext _ctx;
	private readonly Vk _vk;
	private readonly VulkanTechniqueCache _techniques;
	private readonly IReadOnlyDictionary<string, VulkanTechniqueTemplate> _registry;
	// Set per active graph by the engine (SetTransients): a graph swap rebinds this to the new graph's transients
	// while the rest of the context (samplers, technique cache, the registry) is reused.
	private VulkanTransientResources _resources = null!;
	private readonly VulkanDescriptorAllocator _descriptors;
	private readonly VulkanMeshStore _meshes;
	private readonly VulkanTextureStore _textures;
	private readonly MaterialStore _materials;
	private readonly VulkanInstanceUploadBuffer _instanceUpload;
	// Per-frame host-visible ring for the uniform seam (CSM matrices, lighting params, wind) — the same
	// retire-not-free ring as the instance stream, but created UniformBufferBit and aligned to 256 (≥ any
	// device's minUniformBufferOffsetAlignment). Bound as the last descriptor of a technique that reflects a UBO.
	private readonly VulkanInstanceUploadBuffer _uniformUpload;
	// Per-frame host-visible ring for the bone palette the compute pre-skin pass reads as a structured
	// buffer — StorageBufferBit, 256-aligned (≥ minStorageBufferOffsetAlignment).
	private readonly VulkanInstanceUploadBuffer _storageUpload;
	// Per-frame host-visible rings for ctx.DrawList's dynamic geometry — a vertex ring and an index ring, the same
	// retire-not-free staging as the instance stream, so several draw lists in one frame append at successive
	// offsets rather than clobber each other.
	private readonly VulkanInstanceUploadBuffer _listVtx;
	private readonly VulkanInstanceUploadBuffer _listIdx;
	// Renderer-private offscreen targets backing UI group-opacity layers (BeginLayer/EndLayer), below the graph seam.
	private readonly VulkanLayerTargets _layerTargets;
	// Reused across calls so an opacity layer never allocates per frame.
	private readonly List<(Image Image, ImageView View, Half4 Tint)> _layerStack = [];
	// The current graphics pass's colour attachment views + render area, captured by the engine before the pass
	// records, so a draw-list layer can suspend and resume the pass's dynamic-rendering scope.
	private readonly ImageView[] _scopeColorViews = new ImageView[MaxColorFormats];
	private int _scopeColorCount;
	private Extent2D _scopeArea;
	private readonly Sampler _nearestSampler;
	private readonly Sampler _linearSampler;
	// Repeat-addressing, mip-sampling samplers for material textures (tiled across geometry) — a linear and a
	// nearest variant, selected by the technique's LinearFilter flag exactly as the DirectX12 static sampler is
	// (Wrap + MinMagMip{Linear,Point}). The existing nearest/linear samplers above clamp (post passes never tile).
	private readonly Sampler _repeatLinearSampler;
	private readonly Sampler _repeatNearestSampler;
	// Additional addressing modes: mirror-repeat, clamp-to-border (opaque black), mirror-clamp-to-edge.
	private readonly Sampler _mirrorLinearSampler;
	private readonly Sampler _mirrorNearestSampler;
	private readonly Sampler _borderLinearSampler;
	private readonly Sampler _borderNearestSampler;
	private readonly Sampler _mirrorEdgeLinearSampler;
	private readonly Sampler _mirrorEdgeNearestSampler;

	// This frame's draw lists, pulled from FrameInputs.DrawLists and surfaced as ctx.DrawLists; a producer (the UI
	// painter) looks one up by id and submits it through DrawList.
	private IReadOnlyList<(DrawListId Id, DrawListData Data)> _drawLists = [];

	private Extent2D _fbExtent;
	// Per-draw cache keyed by (name + attachment formats); keeps the hot path off VulkanTechniqueCache's
	// per-call SPIR-V file read. Misses fall through to VulkanTechniqueCache (which bakes + content-hashes once).
	// The key is a value type (PipelineKey) so the per-draw lookup allocates nothing (zero GC pressure
	// in the render loop) — the old interpolated-string key heap-allocated on every draw.
	private readonly Dictionary<PipelineKey, VulkanTechnique> _baked = [];

	private CommandBuffer _cmd;
	// The current pass's color formats, stored inline (not a heap array) so BeginPass copies into them per
	// pass without allocating; _colorCount is how many of the MaxColorFormats slots are live.
	private ColorFormatBuffer _colorFormats;
	private int _colorCount;
	private VkFormat _depthFormat;
	private bool _depthReadOnly;
	private int _frameSlot;
	private Camera _view;
	private IReadOnlyDictionary<string, Camera> _views = new Dictionary<string, Camera>();

	public float DeltaTime { get; private set; }

	public float Time { get; private set; }

	public Camera View => _view;

	public IReadOnlyDictionary<string, Camera> Views => _views;

	public bool HdrOutput { get; private set; }

	public IReadOnlyList<(DrawListId Id, DrawListData Data)> DrawLists => _drawLists;

	public VulkanRenderContext(
		VulkanContext ctx,
		VulkanTechniqueCache techniques,
		IReadOnlyDictionary<string, VulkanTechniqueTemplate> registry,
		VulkanDescriptorAllocator descriptors,
		VulkanMeshStore meshes,
		VulkanTextureStore textures,
		MaterialStore materials,
		VMASharp.VulkanMemoryAllocator allocator,
		int framesInFlight)
	{
		_ctx = ctx;
		_vk = ctx.Vk;
		_techniques = techniques;
		_registry = registry;
		_descriptors = descriptors;
		_meshes = meshes;
		_textures = textures;
		_materials = materials;
		_instanceUpload = new VulkanInstanceUploadBuffer(ctx, allocator, framesInFlight);
		_uniformUpload = new VulkanInstanceUploadBuffer(ctx, allocator, framesInFlight, BufferUsageFlags.UniformBufferBit, alignment: 256);
		_storageUpload = new VulkanInstanceUploadBuffer(ctx, allocator, framesInFlight, BufferUsageFlags.StorageBufferBit, alignment: 256);
		_listVtx = new VulkanInstanceUploadBuffer(ctx, allocator, framesInFlight, BufferUsageFlags.VertexBufferBit);
		_listIdx = new VulkanInstanceUploadBuffer(ctx, allocator, framesInFlight, BufferUsageFlags.IndexBufferBit);
		_layerTargets = new VulkanLayerTargets(ctx, allocator, framesInFlight);
		_nearestSampler = CreateSampler(Filter.Nearest);
		_linearSampler = CreateSampler(Filter.Linear);
		_repeatLinearSampler = CreateSampler(Filter.Linear, SamplerAddressMode.Repeat);
		_repeatNearestSampler = CreateSampler(Filter.Nearest, SamplerAddressMode.Repeat);
		_mirrorLinearSampler = CreateSampler(Filter.Linear, SamplerAddressMode.MirroredRepeat);
		_mirrorNearestSampler = CreateSampler(Filter.Nearest, SamplerAddressMode.MirroredRepeat);
		_borderLinearSampler = CreateSampler(Filter.Linear, SamplerAddressMode.ClampToBorder);
		_borderNearestSampler = CreateSampler(Filter.Nearest, SamplerAddressMode.ClampToBorder);
		_mirrorEdgeLinearSampler = CreateSampler(Filter.Linear, SamplerAddressMode.MirrorClampToEdge);
		_mirrorEdgeNearestSampler = CreateSampler(Filter.Nearest, SamplerAddressMode.MirrorClampToEdge);
	}

	/// <summary>Bind the context to the active graph's transients — called by the engine on each <c>SetGraph</c>.</summary>
	public void SetTransients(VulkanTransientResources resources) => _resources = resources;

	/// <summary>Drop the per-(name,formats) baked-pipeline cache so the next draw re-bakes (shader hot-reload).
	/// The VulkanTechnique objects are owned by the VulkanTechniqueCache; the engine resets that separately after draining.</summary>
	public void ClearBaked() => _baked.Clear();

	/// <summary>Bind the context to this frame's command buffer, frame slot, views, timing, HDR-output state, and draw lists before any pass records.</summary>
	public void BeginFrame(CommandBuffer cmd, float deltaTime, float time, int frameSlot, Camera view, IReadOnlyDictionary<string, Camera> views, IReadOnlyList<(DrawListId Id, DrawListData Data)> drawLists, Extent2D fbExtent, bool hdrOutput)
	{
		_cmd = cmd;
		DeltaTime = deltaTime;
		Time = time;
		_frameSlot = frameSlot;
		_view = view;
		_views = views;
		_drawLists = drawLists;
		_fbExtent = fbExtent;
		HdrOutput = hdrOutput;
		_instanceUpload.BeginFrame(frameSlot); // reset this slot's per-frame instance staging
		_uniformUpload.BeginFrame(frameSlot);  // and its per-frame uniform staging
		_storageUpload.BeginFrame(frameSlot);  // and the bone-palette staging
		_listVtx.BeginFrame(frameSlot);        // and the draw-list geometry staging
		_listIdx.BeginFrame(frameSlot);
	}

	// The parameter buffers (ResourceKind.Constants) the current pass .Reads, in order. The engine resolves them
	// from the pass's ReadUniform edges in BeginPass; FullScreen binds them to the technique's uniform slots 0..K-1
	// (an in-closure push fills slot K). MaxUniformBuffers caps the per-pass count.
	public const int MaxUniformBuffers = 8;
	private readonly int[] _activeConstantsIds = new int[MaxUniformBuffers];
	private int _activeConstantsCount;

	// The app-writable storage buffers (ResourceKind.Storage) the current pass .Reads, in order — bound to the
	// technique's storage-buffer slots (after the sampled inputs, before the uniform slots).
	public const int MaxStorageBuffers = 8;
	private readonly int[] _activeStorageIds = new int[MaxStorageBuffers];
	private int _activeStorageCount;

	/// <summary>
	/// Set the attachment formats of the current pass — the formats its techniques bake against — plus whether
	/// the depth attachment is bound read-only (<c>.ReadsDepth</c>): a read-only pass bakes with depth writes
	/// disabled so a transparent draw tests against, but does not disturb, the opaque depth.
	/// <paramref name="constantsIds"/> / <paramref name="storageIds"/> are the parameter and storage buffers the
	/// pass <c>.Reads</c> (in order), bound to the technique's uniform and storage-buffer slots at draw time.
	/// </summary>
	public void BeginPass(ReadOnlySpan<VkFormat> colorFormats, VkFormat depthFormat, bool depthReadOnly, ReadOnlySpan<int> constantsIds = default, ReadOnlySpan<int> storageIds = default)
	{
		// Copy into the inline buffer (no allocation) rather than .ToArray(); the incoming span points into
		// the executor's stackalloc, which is gone once this returns. Throw — never clamp — on an over-wide
		// pass: silently dropping formats would bake a pipeline with fewer attachments than the pass writes.
		if (colorFormats.Length > MaxColorFormats)
			throw new VulkanException($"A pass has {colorFormats.Length} color attachments; the pipeline-key buffer holds {MaxColorFormats}.");
		_colorCount = colorFormats.Length;
		colorFormats.CopyTo(((Span<VkFormat>)_colorFormats)[.._colorCount]);
		_depthFormat = depthFormat;
		_depthReadOnly = depthReadOnly;
		_activeConstantsCount = Math.Min(constantsIds.Length, MaxUniformBuffers);
		constantsIds[.._activeConstantsCount].CopyTo(_activeConstantsIds);
		_activeStorageCount = Math.Min(storageIds.Length, MaxStorageBuffers);
		storageIds[.._activeStorageCount].CopyTo(_activeStorageIds);
	}

	/// <summary>
	/// Capture the current graphics pass's colour attachment views + render area before its <c>.Record</c> runs, so a
	/// <see cref="DrawList{TVertex}"/> group-opacity layer can suspend the pass's dynamic-rendering scope, render into
	/// an offscreen target, and resume the pass scope to composite the result back.
	/// </summary>
	public void SetColorScope(ReadOnlySpan<ImageView> colorViews, Extent2D area)
	{
		_scopeColorCount = Math.Min(colorViews.Length, _scopeColorViews.Length);
		for (var i = 0; i < _scopeColorCount; i++)
			_scopeColorViews[i] = colorViews[i];
		_scopeArea = area;
	}

	public void FullScreen(string technique, params ReadOnlySpan<ResourceHandle> inputs) =>
		FullScreen(technique, default, inputs);

	public void FullScreen(string technique, ReadOnlySpan<float> push, params ReadOnlySpan<ResourceHandle> inputs)
	{
		var baked = Bake(technique);
		var template = _registry[technique];
		_vk.CmdBindPipeline(_cmd, PipelineBindPoint.Graphics, baked.Pipeline);

		// The per-pass data channel. The pass's .Reads Constants buffers bind to the technique's reflected uniform
		// slots 0..K-1; an in-closure push then fills slot K if a reflected uniform slot remains (uniform seam:
		// CSM matrices, lighting params; no 16-float cap), or rides the fixed scalar push range when the technique
		// declares no uniform buffer.
		Span<UniformBinding> ubos = stackalloc UniformBinding[MaxUniformBuffers];
		var uboCount = 0;
		for (var j = 0; j < _activeConstantsCount && uboCount < MaxUniformBuffers; j++)
			ubos[uboCount++] = new UniformBinding(_resources.GetConstantBuffer(_frameSlot, _activeConstantsIds[j]), 0, Vk.WholeSize);
		if (push.Length > 0)
		{
			if (template.UniformBufferCount > uboCount && uboCount < MaxUniformBuffers)
			{
				ubos[uboCount++] = UploadUniforms(System.Runtime.InteropServices.MemoryMarshal.AsBytes(push));
			}
			else if (template.UniformBufferCount == 0)
			{
				// The fragment scalar channel: pad to the layout's fixed range so the shader always reads defined
				// memory, then push to the fragment stage at offset 0.
				var block = stackalloc float[16];
				for (var i = 0; i < push.Length && i < 16; i++)
					block[i] = push[i];
				_vk.CmdPushConstants(_cmd, baked.Layout, ShaderStageFlags.FragmentBit, 0, (uint)VulkanTechnique.FragmentPushSize, block);
			}
		}

		// The app-writable storage buffers the pass .Reads bind after the sampled inputs, before the uniform slots.
		Span<Silk.NET.Vulkan.Buffer> storageBuffers = stackalloc Silk.NET.Vulkan.Buffer[MaxStorageBuffers];
		var storageCount = 0;
		for (var j = 0; j < _activeStorageCount && storageCount < MaxStorageBuffers; j++)
			storageBuffers[storageCount++] = _resources.GetStorageBuffer(_frameSlot, _activeStorageIds[j]);

		if (inputs.Length > 0 || storageCount > 0 || uboCount > 0)
			BindSampledInputs(baked, inputs, template.LinearFilter, storageBuffers[..storageCount], ubos[..uboCount]);

		_vk.CmdDraw(_cmd, 3, 1, 0, 0); // 3 fabricated verts cover the target; no vertex buffer
	}

	/// <summary>Resolve an inspectable graph resource to a draw-list-bindable texture handle for this frame (resource
	/// viewer). The id is offset by <see cref="InspectableResource.DebugTextureBase"/> so <see cref="DrawList{TVertex}"/> samples the live
	/// transient image rather than a loaded texture; the dimensions are the resource's current size.</summary>
	public Texture? ResolveInspectable(int resourceId)
	{
		if (resourceId < 0)
			return null;
		ref var image = ref _resources.Get(_frameSlot, resourceId);
		return new Texture(resourceId + InspectableResource.DebugTextureBase, (int)image.Extent.Width, (int)image.Extent.Height);
	}

	// The technique's shape (samples an input? reads a vertex buffer?) comes from its registered template;
	// the attachment formats are the current pass's. So one template used in passes with different targets
	// caches distinct pipelines, and the call site only needs the name. The local cache keeps the per-draw
	// path off VulkanTechniqueCache's file read.
	private VulkanTechnique Bake(string technique)
	{
		if (!_registry.TryGetValue(technique, out var template))
			throw new VulkanException($"No technique registered under the name '{technique}'.");

		var colorFormats = ((ReadOnlySpan<VkFormat>)_colorFormats)[.._colorCount];
		var key = new PipelineKey(technique, colorFormats, _depthFormat, _depthReadOnly);
		if (_baked.TryGetValue(key, out var cached))
			return cached;

		// A pass that reads depth read-only (.ReadsDepth) bakes with depth writes off; one that writes it
		// (.WritesDepth, or no depth at all) keeps writes on. Materializing the Format[] here (cache miss only)
		// is a one-time warmup allocation per unique pipeline, not per-frame churn.
		var baked = _techniques.GetOrBake(new VulkanTechniqueDescription(
			template.Spirv, template.VertexEntry, template.FragmentEntry, colorFormats.ToArray(),
			template.SampleCount, template.HasVertexInput, _depthFormat, template.Blend, DepthWrite: !_depthReadOnly,
			HasFragmentPush: template.HasFragmentPush, IsCompute: template.IsCompute, StorageCount: template.StorageCount,
			StorageBufferCount: template.StorageBufferCount,
			Layout: template.Layout, Instance: template.Instance, UniformBufferCount: template.UniformBufferCount,
			HasListPush: template.HasListPush, Cull: template.Cull), technique);
		// Note: template.Blend / LinearFilter / HasFragmentPush need no extra key term — Blend and
		// HasFragmentPush are folded into the VulkanTechniqueDescription hash, and LinearFilter only affects the
		// descriptor's sampler, not the pipeline.
		_baked[key] = baked;
		// Name the baked pipeline for GPU debuggers (once per unique pipeline; [Conditional] — stripped without
		// UseNamedGPUObjects). Distinct target-format variants of one technique share the logical name.
		_ctx.SetObjectName(ObjectType.Pipeline, baked.Pipeline.Handle, $"pipeline:{technique}");
		return baked;
	}

	// Cap on a pass's color attachments — well above any realistic device maxColorAttachments (commonly 8),
	// so PipelineKey's inline buffer never overflows in practice; BeginPass throws if a pass exceeds it.
	private const int MaxColorFormats = 16;

	// A fixed-size, inline (non-heap) buffer of color formats — the variable-length part of PipelineKey,
	// stored by value so the key allocates nothing.
	[InlineArray(MaxColorFormats)]
	private struct ColorFormatBuffer
	{
		private VkFormat _element0;
	}

	// Value-type, allocation-free replacement for the old interpolated-string pipeline-cache key. Distinct
	// attachment formats must map to distinct pipelines, so Equals compares the full key — never a hash-only
	// collapse that could alias a wrong-format pipeline.
	private struct PipelineKey : IEquatable<PipelineKey>
	{
		private readonly string _technique;
		private ColorFormatBuffer _colorFormats;
		private readonly int _colorCount;
		private readonly VkFormat _depthFormat;
		private readonly bool _depthReadOnly;

		public PipelineKey(string technique, ReadOnlySpan<VkFormat> colorFormats, VkFormat depthFormat, bool depthReadOnly)
		{
			// Bake only ever passes a span already sliced to <= MaxColorFormats by BeginPass's guard, so this
			// is an internal-contract check — elided in Release, costing nothing on the per-draw hot path.
			Debug.Assert(colorFormats.Length <= MaxColorFormats, "PipelineKey color formats exceed MaxColorFormats.");
			_technique = technique;
			_colorCount = colorFormats.Length;
			for (var i = 0; i < colorFormats.Length; i++)
				_colorFormats[i] = colorFormats[i];
			_depthFormat = depthFormat;
			_depthReadOnly = depthReadOnly;
		}

		public bool Equals(PipelineKey other)
		{
			if (_colorCount != other._colorCount
				|| _depthFormat != other._depthFormat
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
			hash.Add(_depthFormat);
			hash.Add(_depthReadOnly);
			return hash.ToHashCode();
		}
	}

	// Nearest keeps the low-res pixel grid crisp (the upscale blit); linear gives bloom's downsample/upsample
	// the smooth filtering a chunky pixel glow still wants internally. The sampler is part of the combined
	// image-sampler descriptor, so the technique's LinearFilter flag just selects which one FullScreen binds.
	private Sampler CreateSampler(Filter filter, SamplerAddressMode address = SamplerAddressMode.ClampToEdge)
	{
		var mip = filter == Filter.Linear ? SamplerMipmapMode.Linear : SamplerMipmapMode.Nearest;
		SamplerCreateInfo info = new()
		{
			SType = StructureType.SamplerCreateInfo,
			MagFilter = filter,
			MinFilter = filter,
			MipmapMode = mip,
			AddressModeU = address,
			AddressModeV = address,
			AddressModeW = address,
			MaxLod = Vk.LodClampNone,
		};
		_vk.CreateSampler(_ctx.Device, in info, null, out var sampler).Check("vkCreateSampler");
		return sampler;
	}

	public void Dispose()
	{
		_instanceUpload.Dispose();
		_uniformUpload.Dispose();
		_storageUpload.Dispose();
		_listVtx.Dispose();
		_listIdx.Dispose();
		_layerTargets.Dispose();
		if (_nearestSampler.Handle != 0)
			_vk.DestroySampler(_ctx.Device, _nearestSampler, null);
		if (_linearSampler.Handle != 0)
			_vk.DestroySampler(_ctx.Device, _linearSampler, null);
		if (_repeatLinearSampler.Handle != 0)
			_vk.DestroySampler(_ctx.Device, _repeatLinearSampler, null);
		if (_repeatNearestSampler.Handle != 0)
			_vk.DestroySampler(_ctx.Device, _repeatNearestSampler, null);
		if (_mirrorLinearSampler.Handle != 0)
			_vk.DestroySampler(_ctx.Device, _mirrorLinearSampler, null);
		if (_mirrorNearestSampler.Handle != 0)
			_vk.DestroySampler(_ctx.Device, _mirrorNearestSampler, null);
		if (_borderLinearSampler.Handle != 0)
			_vk.DestroySampler(_ctx.Device, _borderLinearSampler, null);
		if (_borderNearestSampler.Handle != 0)
			_vk.DestroySampler(_ctx.Device, _borderNearestSampler, null);
		if (_mirrorEdgeLinearSampler.Handle != 0)
			_vk.DestroySampler(_ctx.Device, _mirrorEdgeLinearSampler, null);
		if (_mirrorEdgeNearestSampler.Handle != 0)
			_vk.DestroySampler(_ctx.Device, _mirrorEdgeNearestSampler, null);
	}
}
