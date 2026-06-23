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
/// callbacks stay the pure, capture-free closures the design wants (Planning.md §8).
/// </summary>
internal sealed unsafe class VulkanRenderContext : IRenderContext, IDisposable
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
	// M15-0: per-frame host-visible ring for the uniform seam (CSM matrices, lighting params, wind) — the same
	// retire-not-free ring as the instance stream, but created UniformBufferBit and aligned to 256 (≥ any
	// device's minUniformBufferOffsetAlignment). Bound as the last descriptor of a technique that reflects a UBO.
	private readonly VulkanInstanceUploadBuffer _uniformUpload;
	// M15-2: per-frame host-visible ring for the bone palette the compute pre-skin pass reads as a structured
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

	// This frame's draw lists, pulled from FrameInputs.DrawLists and surfaced as ctx.DrawLists; a producer (the UI
	// painter) looks one up by id and submits it through DrawList.
	private IReadOnlyList<(DrawListId Id, DrawListData Data)> _drawLists = [];

	private Extent2D _fbExtent;
	// Per-draw cache keyed by (name + attachment formats); keeps the hot path off VulkanTechniqueCache's
	// per-call SPIR-V file read. Misses fall through to VulkanTechniqueCache (which bakes + content-hashes once).
	// The key is a value type (PipelineKey) so the per-draw lookup allocates nothing (Planning.md §8: 0 GC
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
	}

	/// <summary>Bind the context to the active graph's transients — called by the engine on each <c>SetGraph</c>.</summary>
	public void SetTransients(VulkanTransientResources resources) => _resources = resources;

	/// <summary>Drop the per-(name,formats) baked-pipeline cache so the next draw re-bakes (shader hot-reload, M13).
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
		_uniformUpload.BeginFrame(frameSlot);  // and its per-frame uniform staging (M15-0)
		_storageUpload.BeginFrame(frameSlot);  // and the bone-palette staging (M15-2)
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
		// slots 0..K-1; an in-closure push then fills slot K if a reflected uniform slot remains (M15-0 — CSM
		// matrices, lighting params; no 16-float cap), or rides the fixed scalar push range when the technique
		// declares no uniform buffer.
		Span<UniformBinding> ubos = stackalloc UniformBinding[MaxUniformBuffers];
		var uboCount = 0;
		for (var j = 0; j < _activeConstantsCount && uboCount < MaxUniformBuffers; j++)
			ubos[uboCount++] = new UniformBinding(_resources.GetConstantBuffer(_frameSlot, _activeConstantsIds[j]), 0, Vk.WholeSize);
		if (push.Length > 0)
		{
			if (template.UniformBufferCount > uboCount && uboCount < MaxUniformBuffers)
			{
				ubos[uboCount++] = UploadUniforms(push);
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

	public void Draw(Mesh mesh, string technique) => Draw(mesh, technique, _view);

	public void Draw(Mesh mesh, string technique, Camera view)
	{
		var baked = Bake(technique);
		var mvp = view.ViewProjection; // model is identity for now; per-object transforms come later
		_vk.CmdBindPipeline(_cmd, PipelineBindPoint.Graphics, baked.Pipeline);
		_vk.CmdPushConstants(_cmd, baked.Layout, ShaderStageFlags.VertexBit, 0, (uint)sizeof(Matrix4x4), &mvp);
		_meshes.Draw(_cmd, mesh.Id); // binds the mesh's vertex/index buffers and records the indexed draw
	}

	public void Draw(Mesh mesh, Material material, string technique) => Draw(mesh, material, technique, _view);

	public void Draw(Mesh mesh, Material material, string technique, Camera view)
	{
		var baked = Bake(technique);
		var template = _registry[technique];
		var mvp = view.ViewProjection;
		_vk.CmdBindPipeline(_cmd, PipelineBindPoint.Graphics, baked.Pipeline);
		_vk.CmdPushConstants(_cmd, baked.Layout, ShaderStageFlags.VertexBit, 0, (uint)sizeof(Matrix4x4), &mvp);

		// Bind the material's textures into the technique's reflected sampler slots 0..N-1 — the same
		// combined-image-sampler mechanism FullScreen uses, but the views come from the VulkanTextureStore (loaded
		// textures), not the transients. A technique that reflects nothing (depth-only/shadow) skips this and the
		// material is ignored. Material textures sample with Repeat addressing when the technique asks for it.
		if (template.SampleCount > 0)
			BindMaterialTextures(baked, template, material, technique);

		_meshes.Draw(_cmd, mesh.Id);
	}

	public void Dispatch(string technique, (uint X, uint Y, uint Z) groups, ReadOnlySpan<float> push, ReadOnlySpan<ResourceHandle> sampled, ReadOnlySpan<ResourceHandle> storage)
	{
		var baked = Bake(technique);
		_vk.CmdBindPipeline(_cmd, PipelineBindPoint.Compute, baked.Pipeline);

		if (push.Length > 0)
		{
			var block = stackalloc float[16];
			for (var i = 0; i < push.Length && i < 16; i++)
				block[i] = push[i];
			_vk.CmdPushConstants(_cmd, baked.Layout, ShaderStageFlags.ComputeBit, 0, (uint)VulkanTechnique.FragmentPushSize, block);
		}

		var total = sampled.Length + storage.Length;
		if (total > 0)
		{
			// Sampled inputs use the technique's declared filter (matching the Draw/FullScreen path and DirectX12's
			// static sampler) — not a hardcoded nearest, which silently diverged from DirectX12 for a LinearFilter pass.
			var sampler = _registry[technique].LinearFilter ? _linearSampler : _nearestSampler;
			// Binding order matches the layout: sampled inputs (combined image samplers, ShaderReadOnly), then
			// storage resources — a storage IMAGE (GENERAL) or, for a Buffer-kind resource, a storage BUFFER.
			var set = _descriptors.Allocate(baked.DescriptorSetLayout);
			var imageInfos = stackalloc DescriptorImageInfo[total];
			var bufferInfos = stackalloc DescriptorBufferInfo[total];
			var writes = stackalloc WriteDescriptorSet[total];
			for (var i = 0; i < total; i++)
			{
				var write = new WriteDescriptorSet
				{
					SType = StructureType.WriteDescriptorSet,
					DstSet = set,
					DstBinding = (uint)i,
					DescriptorCount = 1,
				};
				if (i < sampled.Length)
				{
					ref var image = ref _resources.Get(_frameSlot, sampled[i].Id);
					imageInfos[i] = new DescriptorImageInfo { Sampler = sampler, ImageView = image.View, ImageLayout = ImageLayout.ShaderReadOnlyOptimal };
					write.DescriptorType = DescriptorType.CombinedImageSampler;
					write.PImageInfo = &imageInfos[i];
				}
				else
				{
					var handle = storage[i - sampled.Length];
					if (handle.Kind == ResourceKind.Buffer)
					{
						bufferInfos[i] = new DescriptorBufferInfo { Buffer = _resources.GetBuffer(_frameSlot, handle.Id), Offset = 0, Range = Vk.WholeSize };
						write.DescriptorType = DescriptorType.StorageBuffer;
						write.PBufferInfo = &bufferInfos[i];
					}
					else
					{
						ref var image = ref _resources.Get(_frameSlot, handle.Id);
						imageInfos[i] = new DescriptorImageInfo { ImageView = image.View, ImageLayout = ImageLayout.General };
						write.DescriptorType = DescriptorType.StorageImage;
						write.PImageInfo = &imageInfos[i];
					}
				}
				writes[i] = write;
			}
			_vk.UpdateDescriptorSets(_ctx.Device, (uint)total, writes, 0, null);
			_vk.CmdBindDescriptorSets(_cmd, PipelineBindPoint.Compute, baked.Layout, 0, 1, in set, 0, null);
		}

		_vk.CmdDispatch(_cmd, groups.X, groups.Y, groups.Z);
	}

	public void DrawInstanced(string technique, BufferHandle instances, int vertexCount, int instanceCount)
	{
		var baked = Bake(technique);
		var mvp = _view.ViewProjection;
		_vk.CmdBindPipeline(_cmd, PipelineBindPoint.Graphics, baked.Pipeline);
		_vk.CmdPushConstants(_cmd, baked.Layout, ShaderStageFlags.VertexBit, 0, (uint)sizeof(Matrix4x4), &mvp);
		// The buffer IS the per-instance vertex stream (binding 0); the technique fabricates vertexCount verts per
		// instance from SV_VertexID. The pass's .ReadsVertex put it in the vertex-input stage.
		var vertexBuffer = _resources.GetBuffer(_frameSlot, instances.Id);
		ulong offset = 0;
		_vk.CmdBindVertexBuffers(_cmd, 0, 1, in vertexBuffer, in offset);
		_vk.CmdDraw(_cmd, (uint)vertexCount, (uint)instanceCount, 0, 0);
	}

	public void DrawInstanced<T>(Mesh mesh, string technique, ReadOnlySpan<T> instances) where T : unmanaged
	{
		if (instances.Length == 0)
			return;
		var baked = Bake(technique);
		var mvp = _view.ViewProjection; // the per-instance world matrix rides in the instance stream
		_vk.CmdBindPipeline(_cmd, PipelineBindPoint.Graphics, baked.Pipeline);
		_vk.CmdPushConstants(_cmd, baked.Layout, ShaderStageFlags.VertexBit, 0, (uint)sizeof(Matrix4x4), &mvp);
		var (buffer, offset) = _instanceUpload.Upload(_frameSlot, System.Runtime.InteropServices.MemoryMarshal.AsBytes(instances));
		_meshes.DrawInstanced(_cmd, mesh.Id, buffer, offset, (uint)instances.Length);
	}

	public void DrawInstanced(Mesh mesh, string technique, BufferHandle instances, int count)
	{
		var baked = Bake(technique);
		var mvp = _view.ViewProjection;
		_vk.CmdBindPipeline(_cmd, PipelineBindPoint.Graphics, baked.Pipeline);
		_vk.CmdPushConstants(_cmd, baked.Layout, ShaderStageFlags.VertexBit, 0, (uint)sizeof(Matrix4x4), &mvp);
		// The instance stream is a graph storage buffer a compute filled; the pass's .ReadsVertex derived the barrier.
		var buffer = _resources.GetBuffer(_frameSlot, instances.Id);
		_meshes.DrawInstanced(_cmd, mesh.Id, buffer, 0ul, (uint)count);
	}

	public void DispatchSkin(string technique, Mesh skinnedMesh, ReadOnlySpan<Matrix4x4> palette, BufferHandle output, int vertexCount)
	{
		if (vertexCount <= 0 || palette.Length == 0)
			return;
		var baked = Bake(technique);
		_vk.CmdBindPipeline(_cmd, PipelineBindPoint.Compute, baked.Pipeline);

		// vertexCount rides the compute scalar push (as a float — exact for any realistic count).
		var block = stackalloc float[16];
		block[0] = vertexCount;
		_vk.CmdPushConstants(_cmd, baked.Layout, ShaderStageFlags.ComputeBit, 0, (uint)VulkanTechnique.FragmentPushSize, block);

		// Three storage buffers at bindings 0,1,2: bind-pose (mesh, long-lived) · palette (per-frame ring) · output
		// (the transient the pass .ReadWrites). None but the output is a graph resource, so DispatchSkin writes its
		// own set rather than going through the generic Dispatch storage path.
		var paletteBytes = System.Runtime.InteropServices.MemoryMarshal.AsBytes(palette);
		var (paletteBuffer, paletteOffset) = _storageUpload.Upload(_frameSlot, paletteBytes);
		var inBuffer = _meshes.BindPoseBuffer(skinnedMesh.Id);
		var outBuffer = _resources.GetBuffer(_frameSlot, output.Id);

		var set = _descriptors.Allocate(baked.DescriptorSetLayout);
		var bufferInfos = stackalloc DescriptorBufferInfo[3];
		bufferInfos[0] = new DescriptorBufferInfo { Buffer = inBuffer, Offset = 0, Range = Vk.WholeSize };
		bufferInfos[1] = new DescriptorBufferInfo { Buffer = paletteBuffer, Offset = paletteOffset, Range = (ulong)paletteBytes.Length };
		bufferInfos[2] = new DescriptorBufferInfo { Buffer = outBuffer, Offset = 0, Range = Vk.WholeSize };
		var writes = stackalloc WriteDescriptorSet[3];
		for (var i = 0; i < 3; i++)
			writes[i] = new WriteDescriptorSet
			{
				SType = StructureType.WriteDescriptorSet,
				DstSet = set,
				DstBinding = (uint)i,
				DescriptorCount = 1,
				DescriptorType = DescriptorType.StorageBuffer,
				PBufferInfo = &bufferInfos[i],
			};
		_vk.UpdateDescriptorSets(_ctx.Device, 3, writes, 0, null);
		_vk.CmdBindDescriptorSets(_cmd, PipelineBindPoint.Compute, baked.Layout, 0, 1, in set, 0, null);

		_vk.CmdDispatch(_cmd, (uint)((vertexCount + 63) / 64), 1, 1);
	}

	public void DrawSkinned(Mesh mesh, BufferHandle skinnedVertices, string technique) => DrawSkinned(mesh, skinnedVertices, technique, _view);

	public void DrawSkinned(Mesh mesh, BufferHandle skinnedVertices, string technique, Camera view)
	{
		var baked = Bake(technique);
		var mvp = view.ViewProjection;
		_vk.CmdBindPipeline(_cmd, PipelineBindPoint.Graphics, baked.Pipeline);
		_vk.CmdPushConstants(_cmd, baked.Layout, ShaderStageFlags.VertexBit, 0, (uint)sizeof(Matrix4x4), &mvp);
		// The per-vertex stream is the compute's skinned output (MeshStandard-shaped); the mesh contributes its index buffer.
		var vertexBuffer = _resources.GetBuffer(_frameSlot, skinnedVertices.Id);
		_meshes.DrawSkinned(_cmd, mesh.Id, vertexBuffer);
	}

	public void DrawSkinned(Mesh mesh, BufferHandle skinnedVertices, Material material, string technique) => DrawSkinned(mesh, skinnedVertices, material, technique, _view);

	public void DrawSkinned(Mesh mesh, BufferHandle skinnedVertices, Material material, string technique, Camera view)
	{
		var baked = Bake(technique);
		var template = _registry[technique];
		var mvp = view.ViewProjection;
		_vk.CmdBindPipeline(_cmd, PipelineBindPoint.Graphics, baked.Pipeline);
		_vk.CmdPushConstants(_cmd, baked.Layout, ShaderStageFlags.VertexBit, 0, (uint)sizeof(Matrix4x4), &mvp);
		if (template.SampleCount > 0)
			BindMaterialTextures(baked, template, material, technique);
		var vertexBuffer = _resources.GetBuffer(_frameSlot, skinnedVertices.Id);
		_meshes.DrawSkinned(_cmd, mesh.Id, vertexBuffer);
	}

	public void DrawInstanced<T>(Mesh mesh, Material material, string technique, ReadOnlySpan<T> instances) where T : unmanaged
	{
		if (instances.Length == 0)
			return;
		var baked = Bake(technique);
		var template = _registry[technique];
		var mvp = _view.ViewProjection; // the per-instance world data rides in the instance stream
		_vk.CmdBindPipeline(_cmd, PipelineBindPoint.Graphics, baked.Pipeline);
		_vk.CmdPushConstants(_cmd, baked.Layout, ShaderStageFlags.VertexBit, 0, (uint)sizeof(Matrix4x4), &mvp);
		// Material atlas → the technique's reflected sampler slots, identical to Draw(mesh, material): every
		// instance samples the shared atlas; the per-instance uv-rect (in the stream) picks each quad's region.
		if (template.SampleCount > 0)
			BindMaterialTextures(baked, template, material, technique);
		var (buffer, offset) = _instanceUpload.Upload(_frameSlot, System.Runtime.InteropServices.MemoryMarshal.AsBytes(instances));
		_meshes.DrawInstanced(_cmd, mesh.Id, buffer, offset, (uint)instances.Length);
	}

	public void DrawInstanced<T>(Mesh mesh, Material material, string technique, ReadOnlySpan<T> instances, ReadOnlySpan<float> uniforms) where T : unmanaged
	{
		if (instances.Length == 0)
			return;
		var baked = Bake(technique);
		var template = _registry[technique];
		var mvp = _view.ViewProjection;
		_vk.CmdBindPipeline(_cmd, PipelineBindPoint.Graphics, baked.Pipeline);
		_vk.CmdPushConstants(_cmd, baked.Layout, ShaderStageFlags.VertexBit, 0, (uint)sizeof(Matrix4x4), &mvp);
		// The per-frame uniform block (M15-0) — e.g. foliage wind, read in the vertex stage; bound alongside the
		// material's alpha texture into the technique's reflected slots. The MVP still rides the vertex push.
		var ubo = uniforms.Length > 0 ? UploadUniforms(uniforms) : default;
		if (template.SampleCount > 0 || ubo.Present)
			BindMaterialTextures(baked, template, material, technique, ubo);
		var (buffer, offset) = _instanceUpload.Upload(_frameSlot, System.Runtime.InteropServices.MemoryMarshal.AsBytes(instances));
		_meshes.DrawInstanced(_cmd, mesh.Id, buffer, offset, (uint)instances.Length);
	}

	public void DrawInstanced<T>(Mesh mesh, string technique, ReadOnlySpan<T> instances, ReadOnlySpan<ResourceHandle> sampled) where T : unmanaged
	{
		if (instances.Length == 0)
			return;
		var baked = Bake(technique);
		var mvp = _view.ViewProjection;
		_vk.CmdBindPipeline(_cmd, PipelineBindPoint.Graphics, baked.Pipeline);
		_vk.CmdPushConstants(_cmd, baked.Layout, ShaderStageFlags.VertexBit, 0, (uint)sizeof(Matrix4x4), &mvp);
		// Sampled graph resources (the G-buffer) → sampled slots 0..N-1, identical to FullScreen: each instanced
		// light volume samples the normal/albedo at its covered pixel. The graph put them in ShaderReadOnly.
		if (sampled.Length > 0)
			BindSampledInputs(baked, sampled, _registry[technique].LinearFilter);
		var (buffer, offset) = _instanceUpload.Upload(_frameSlot, System.Runtime.InteropServices.MemoryMarshal.AsBytes(instances));
		_meshes.DrawInstanced(_cmd, mesh.Id, buffer, offset, (uint)instances.Length);
	}

	public void DrawInstanced(string technique, Material material, BufferHandle instances, int vertexCount, int instanceCount)
	{
		var baked = Bake(technique);
		var template = _registry[technique];
		var mvp = _view.ViewProjection;
		_vk.CmdBindPipeline(_cmd, PipelineBindPoint.Graphics, baked.Pipeline);
		_vk.CmdPushConstants(_cmd, baked.Layout, ShaderStageFlags.VertexBit, 0, (uint)sizeof(Matrix4x4), &mvp);
		// The atlas → reflected sampler slots, so the fabricated quads can sub-UV sample per frame.
		if (template.SampleCount > 0)
			BindMaterialTextures(baked, template, material, technique);
		var vertexBuffer = _resources.GetBuffer(_frameSlot, instances.Id);
		ulong offset = 0;
		_vk.CmdBindVertexBuffers(_cmd, 0, 1, in vertexBuffer, in offset);
		_vk.CmdDraw(_cmd, (uint)vertexCount, (uint)instanceCount, 0, 0);
	}

	// UI group-opacity layer markers (UIPrimKind, kept in sync with SUIsei): a BeginLayer/EndLayer pair carries no
	// indices and never reaches the fragment shader — the backend turns it into an offscreen render scope and a
	// composite. KIND_IMAGE is the shader branch the composite blit uses (sample the layer, tint by group alpha).
	private const uint TagBeginLayer = 5;
	private const uint TagEndLayer = 6;
	private const uint KindImage = 4;

	public void DrawList<TVertex>(string technique, ReadOnlySpan<Texture> textures, ReadOnlySpan<TVertex> vertices, ReadOnlySpan<ushort> indices, ReadOnlySpan<DrawCommand> commands, ReadOnlySpan<float> push) where TVertex : unmanaged
	{
		if (commands.IsEmpty || vertices.IsEmpty || indices.IsEmpty || textures.IsEmpty)
			return;
		var baked = Bake(technique);
		var template = _registry[technique];

		// Stage this frame's geometry into the dynamic rings (bump-allocated: several draw lists in one frame
		// append at successive offsets rather than overwrite each other).
		var (vtxBuffer, vtxOffset) = _listVtx.Upload(_frameSlot, System.Runtime.InteropServices.MemoryMarshal.AsBytes(vertices));
		var (idxBuffer, idxOffset) = _listIdx.Upload(_frameSlot, System.Runtime.InteropServices.MemoryMarshal.AsBytes(indices));

		_vk.CmdBindPipeline(_cmd, PipelineBindPoint.Graphics, baked.Pipeline);
		_vk.CmdBindVertexBuffers(_cmd, 0, 1, in vtxBuffer, in vtxOffset);
		_vk.CmdBindIndexBuffer(_cmd, idxBuffer, idxOffset, IndexType.Uint16);

		Viewport viewport = new() { X = 0, Y = 0, Width = _fbExtent.Width, Height = _fbExtent.Height, MinDepth = 0, MaxDepth = 1 };
		_vk.CmdSetViewport(_cmd, 0, 1, in viewport);

		// The shared per-list push fills the block's first slots; each command rewrites the slot right after it with
		// its tag (the draw-list contract). Pad to the layout's fixed range so the shader always reads defined memory.
		const int blockFloats = VulkanTechnique.ListPushSize / sizeof(float);
		var block = stackalloc float[blockFloats];
		var pushLen = Math.Min(push.Length, blockFloats - 1);
		for (var i = 0; i < pushLen; i++)
			block[i] = push[i];

		var sampler = template.Address switch
		{
			SamplerAddress.Repeat => template.LinearFilter ? _repeatLinearSampler : _repeatNearestSampler,
			SamplerAddress.Clamp => template.LinearFilter ? _linearSampler : _nearestSampler,
			_ => throw new VulkanException($"SamplerAddress '{template.Address}' is not mapped to a sampler."),
		};

		// Group-opacity layers are only meaningful on an overlay-vertex list (the only producer of the markers); a
		// scope to suspend; and a surface-pixel→clip push to size the composite quad. Without all three, markers fold
		// to no-ops (a zero-index draw).
		var layerArea = _scopeArea.Width != 0 ? _scopeArea : _fbExtent;
		var layersSupported = Unsafe.SizeOf<TVertex>() == Unsafe.SizeOf<OverlayVertex>()
			&& _scopeColorCount > 0 && _colorCount > 0 && push.Length >= 2 && push[0] != 0 && push[1] != 0;
		var overlay = layersSupported ? System.Runtime.InteropServices.MemoryMarshal.Cast<TVertex, OverlayVertex>(vertices) : default;
		if (layersSupported)
			_layerTargets.Configure(layerArea, _colorFormats[0]);
		_layerStack.Clear();

		foreach (var c in commands)
		{
			if (layersSupported && c.Tag == TagBeginLayer)
			{
				// Suspend the current scope, render the faded subtree into a fresh transparent offscreen target at
				// full alpha (so overlapping children blend against each other, not a faded backdrop).
				_vk.CmdEndRendering(_cmd);
				var target = _layerTargets.Acquire(_frameSlot, _layerStack.Count);
				TransitionImage(target.Image, ImageLayout.Undefined, ImageLayout.ColorAttachmentOptimal,
					PipelineStageFlags2.FragmentShaderBit, AccessFlags2.ShaderSampledReadBit,
					PipelineStageFlags2.ColorAttachmentOutputBit, AccessFlags2.ColorAttachmentWriteBit);
				BeginColorScope(target.View, AttachmentLoadOp.Clear, layerArea);
				_vk.CmdSetViewport(_cmd, 0, 1, in viewport);
				_layerStack.Add((target.Image, target.View, overlay[c.VertexOffset].Color));
				continue;
			}
			if (layersSupported && c.Tag == TagEndLayer)
			{
				if (_layerStack.Count == 0)
					continue;
				var layer = _layerStack[^1];
				_layerStack.RemoveAt(_layerStack.Count - 1);

				// Resolve the layer target, then resume the parent scope (LoadOp.Load — keep what it already holds).
				_vk.CmdEndRendering(_cmd);
				TransitionImage(layer.Image, ImageLayout.ColorAttachmentOptimal, ImageLayout.ShaderReadOnlyOptimal,
					PipelineStageFlags2.ColorAttachmentOutputBit, AccessFlags2.ColorAttachmentWriteBit,
					PipelineStageFlags2.FragmentShaderBit, AccessFlags2.ShaderSampledReadBit);
				if (_layerStack.Count > 0)
					BeginColorScope(_layerStack[^1].View, AttachmentLoadOp.Load, layerArea);
				else
					BeginColorScope(_scopeColorViews.AsSpan(0, _scopeColorCount), AttachmentLoadOp.Load, layerArea);
				_vk.CmdSetViewport(_cmd, 0, 1, in viewport);

				CompositeLayer(baked, layer.View, layer.Tint, sampler, block, pushLen, push, layerArea);

				// Restore the list's geometry for the following commands (the composite bound its own quad buffer).
				_vk.CmdBindVertexBuffers(_cmd, 0, 1, in vtxBuffer, in vtxOffset);
				_vk.CmdBindIndexBuffer(_cmd, idxBuffer, idxOffset, IndexType.Uint16);
				continue;
			}

			var clip = c.ClipRect;
			var minX = MathF.Max(0, clip.X);
			var minY = MathF.Max(0, clip.Y);
			var maxX = MathF.Min(_fbExtent.Width, clip.X + clip.Width);
			var maxY = MathF.Min(_fbExtent.Height, clip.Y + clip.Height);
			if (maxX <= minX || maxY <= minY)
				continue;

			*(uint*)(block + pushLen) = c.Tag;
			_vk.CmdPushConstants(_cmd, baked.Layout, ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit, 0, (uint)VulkanTechnique.ListPushSize, block);

			var texId = textures[Math.Clamp(c.TextureIndex, 0, textures.Length - 1)].Id;
			// An id offset by DebugTextureBase is an inspectable graph resource (the resource viewer): sample this
			// frame's live transient image instead of a loaded texture-store entry.
			var view = texId >= InspectableResource.DebugTextureBase
				? _resources.Get(_frameSlot, texId - InspectableResource.DebugTextureBase).View
				: _textures.View(texId);
			var set = _descriptors.Allocate(baked.DescriptorSetLayout);
			var imageInfo = new DescriptorImageInfo { Sampler = sampler, ImageView = view, ImageLayout = ImageLayout.ShaderReadOnlyOptimal };
			var write = new WriteDescriptorSet
			{
				SType = StructureType.WriteDescriptorSet,
				DstSet = set,
				DstBinding = 0,
				DescriptorCount = 1,
				DescriptorType = DescriptorType.CombinedImageSampler,
				PImageInfo = &imageInfo,
			};
			_vk.UpdateDescriptorSets(_ctx.Device, 1, in write, 0, null);
			_vk.CmdBindDescriptorSets(_cmd, PipelineBindPoint.Graphics, baked.Layout, 0, 1, in set, 0, null);

			Rect2D scissor = new(new Offset2D((int)minX, (int)minY), new Extent2D((uint)(maxX - minX), (uint)(maxY - minY)));
			_vk.CmdSetScissor(_cmd, 0, 1, in scissor);

			_vk.CmdDrawIndexed(_cmd, (uint)c.IndexCount, 1, (uint)c.IndexOffset, c.VertexOffset, 0);
		}

		// Defensive: a well-formed list balances its markers, but never leave a layer scope open — the engine ends
		// exactly one scope after the pass, which must be the parent (screen) scope.
		while (_layerStack.Count > 0)
		{
			_layerStack.RemoveAt(_layerStack.Count - 1);
			_vk.CmdEndRendering(_cmd);
			BeginColorScope(((ReadOnlySpan<ImageView>)_scopeColorViews)[.._scopeColorCount], AttachmentLoadOp.Load, layerArea);
		}
	}

	// Begin a dynamic-rendering scope over the given colour attachment view(s) at the pass render area — used to
	// open a layer's offscreen target (LoadOp.Clear) and to resume the suspended parent scope (LoadOp.Load).
	private void BeginColorScope(ReadOnlySpan<ImageView> views, AttachmentLoadOp load, Extent2D area)
	{
		var color = stackalloc RenderingAttachmentInfo[views.Length];
		for (var i = 0; i < views.Length; i++)
			color[i] = new RenderingAttachmentInfo
			{
				SType = StructureType.RenderingAttachmentInfo,
				ImageView = views[i],
				ImageLayout = ImageLayout.ColorAttachmentOptimal,
				LoadOp = load,
				StoreOp = AttachmentStoreOp.Store,
				ClearValue = default, // transparent-black premultiplied — a layer starts empty
			};
		RenderingInfo info = new()
		{
			SType = StructureType.RenderingInfo,
			RenderArea = new Rect2D(new Offset2D(0, 0), area),
			LayerCount = 1,
			ColorAttachmentCount = (uint)views.Length,
			PColorAttachments = color,
		};
		_vk.CmdBeginRendering(_cmd, in info);
	}

	private void BeginColorScope(ImageView view, AttachmentLoadOp load, Extent2D area) =>
		BeginColorScope(new ReadOnlySpan<ImageView>(in view), load, area);

	// Composite a resolved layer target back into the (now-resumed) parent: one full-surface KIND_IMAGE quad that
	// samples the layer and multiplies by the premultiplied group-alpha tint, so the whole subtree fades once. The
	// HDR paper-white lift is forced off here — the layer's primitives already lifted, and the lift commutes with
	// the premultiplied alpha-over, so re-lifting would double it (the CPU rasterizer is the reference for this).
	private void CompositeLayer(VulkanTechnique baked, ImageView layerView, Half4 tint, Sampler sampler, float* block, int pushLen, ReadOnlySpan<float> push, Extent2D area)
	{
		var coordX = 2f / push[0];
		var coordY = 2f / push[1];
		var custom = default(Vector4);
		Span<OverlayVertex> quad =
		[
			new(new Vector2(0, 0), new Vector2(0, 0), tint, custom),
			new(new Vector2(coordX, 0), new Vector2(1, 0), tint, custom),
			new(new Vector2(coordX, coordY), new Vector2(1, 1), tint, custom),
			new(new Vector2(0, 0), new Vector2(0, 0), tint, custom),
			new(new Vector2(coordX, coordY), new Vector2(1, 1), tint, custom),
			new(new Vector2(0, coordY), new Vector2(0, 1), tint, custom),
		];
		var (quadBuffer, quadOffset) = _listVtx.Upload(_frameSlot, System.Runtime.InteropServices.MemoryMarshal.AsBytes(quad));
		_vk.CmdBindVertexBuffers(_cmd, 0, 1, in quadBuffer, in quadOffset);

		const int blockFloats = VulkanTechnique.ListPushSize / sizeof(float);
		var cblock = stackalloc float[blockFloats];
		for (var i = 0; i < pushLen; i++)
			cblock[i] = block[i];
		if (pushLen > 5)
			cblock[5] = 0f;                    // isHdr off — do not re-lift the already-lifted layer
		*(uint*)(cblock + pushLen) = KindImage;
		_vk.CmdPushConstants(_cmd, baked.Layout, ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit, 0, (uint)VulkanTechnique.ListPushSize, cblock);

		var set = _descriptors.Allocate(baked.DescriptorSetLayout);
		var imageInfo = new DescriptorImageInfo { Sampler = sampler, ImageView = layerView, ImageLayout = ImageLayout.ShaderReadOnlyOptimal };
		var write = new WriteDescriptorSet
		{
			SType = StructureType.WriteDescriptorSet,
			DstSet = set,
			DstBinding = 0,
			DescriptorCount = 1,
			DescriptorType = DescriptorType.CombinedImageSampler,
			PImageInfo = &imageInfo,
		};
		_vk.UpdateDescriptorSets(_ctx.Device, 1, in write, 0, null);
		_vk.CmdBindDescriptorSets(_cmd, PipelineBindPoint.Graphics, baked.Layout, 0, 1, in set, 0, null);

		Rect2D scissor = new(new Offset2D(0, 0), area);
		_vk.CmdSetScissor(_cmd, 0, 1, in scissor);
		_vk.CmdDraw(_cmd, 6, 1, 0, 0);
	}

	// An image-layout transition (sync2) for the renderer-private layer targets — the same shape as the engine's
	// transitions, recorded here because a layer's choreography is the backend's, below the graph's barrier derivation.
	private void TransitionImage(Image image, ImageLayout oldLayout, ImageLayout newLayout,
		PipelineStageFlags2 srcStage, AccessFlags2 srcAccess, PipelineStageFlags2 dstStage, AccessFlags2 dstAccess)
	{
		var barrier = new ImageMemoryBarrier2
		{
			SType = StructureType.ImageMemoryBarrier2,
			SrcStageMask = srcStage,
			SrcAccessMask = srcAccess,
			DstStageMask = dstStage,
			DstAccessMask = dstAccess,
			OldLayout = oldLayout,
			NewLayout = newLayout,
			SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
			DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
			Image = image,
			SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1),
		};
		var dependency = new DependencyInfo
		{
			SType = StructureType.DependencyInfo,
			ImageMemoryBarrierCount = 1,
			PImageMemoryBarriers = &barrier,
		};
		_vk.CmdPipelineBarrier2(_cmd, in dependency);
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

	// Bind sampled graph resources (transients) as combined image samplers at bindings 0..N-1 — the path
	// FullScreen and the instanced light volumes share. The graph put each in ShaderReadOnly before this pass.
	// A present uniform buffer (M15-0) is written as the last binding (index inputs.Length), matching the set
	// layout's category order (samplers, then the UBO).
	private void BindSampledInputs(VulkanTechnique baked, ReadOnlySpan<ResourceHandle> inputs, bool linear, ReadOnlySpan<Silk.NET.Vulkan.Buffer> storageBuffers = default, ReadOnlySpan<UniformBinding> ubos = default)
	{
		var sampler = linear ? _linearSampler : _nearestSampler;
		var set = _descriptors.Allocate(baked.DescriptorSetLayout);
		var count = inputs.Length + storageBuffers.Length + ubos.Length;
		var imageInfos = stackalloc DescriptorImageInfo[inputs.Length];
		var bufferInfos = stackalloc DescriptorBufferInfo[storageBuffers.Length + ubos.Length];
		var writes = stackalloc WriteDescriptorSet[count];
		var w = 0;
		for (var i = 0; i < inputs.Length; i++)
		{
			ref var image = ref _resources.Get(_frameSlot, inputs[i].Id);
			imageInfos[i] = new DescriptorImageInfo
			{
				Sampler = sampler,
				ImageView = image.View,
				ImageLayout = ImageLayout.ShaderReadOnlyOptimal,
			};
			writes[w++] = new WriteDescriptorSet
			{
				SType = StructureType.WriteDescriptorSet,
				DstSet = set,
				DstBinding = (uint)i,
				DescriptorCount = 1,
				DescriptorType = DescriptorType.CombinedImageSampler,
				PImageInfo = &imageInfos[i],
			};
		}
		// Read-only storage buffers (SSBOs) follow the samplers, as StorageBuffer descriptors.
		for (var s = 0; s < storageBuffers.Length; s++)
		{
			bufferInfos[s] = new DescriptorBufferInfo { Buffer = storageBuffers[s], Offset = 0, Range = Vk.WholeSize };
			writes[w++] = new WriteDescriptorSet
			{
				SType = StructureType.WriteDescriptorSet,
				DstSet = set,
				DstBinding = (uint)(inputs.Length + s),
				DescriptorCount = 1,
				DescriptorType = DescriptorType.StorageBuffer,
				PBufferInfo = &bufferInfos[s],
			};
		}
		// Uniform buffers follow the storage buffers.
		for (var u = 0; u < ubos.Length; u++)
		{
			bufferInfos[storageBuffers.Length + u] = ubos[u].Info;
			writes[w++] = new WriteDescriptorSet
			{
				SType = StructureType.WriteDescriptorSet,
				DstSet = set,
				DstBinding = (uint)(inputs.Length + storageBuffers.Length + u),
				DescriptorCount = 1,
				DescriptorType = DescriptorType.UniformBuffer,
				PBufferInfo = &bufferInfos[storageBuffers.Length + u],
			};
		}
		_vk.UpdateDescriptorSets(_ctx.Device, (uint)count, writes, 0, null);
		_vk.CmdBindDescriptorSets(_cmd, PipelineBindPoint.Graphics, baked.Layout, 0, 1, in set, 0, null);
	}

	// Bind a material's loaded textures (VulkanTextureStore views) into the technique's reflected sampler slots 0..N-1,
	// with the technique's address mode + filter. Shared by Draw(mesh, material), textured instancing, and
	// textured particles. The caller checks SampleCount > 0 (or a present UBO). A present uniform buffer (M15-0)
	// is written as the last binding (index SampleCount) — e.g. foliage's wind, read in the vertex stage.
	private void BindMaterialTextures(VulkanTechnique baked, VulkanTechniqueTemplate template, Material material, string technique, in UniformBinding ubo = default)
	{
		var textures = _materials.TexturesOf(material.Id);
		if (textures.Length < template.SampleCount)
			throw new VulkanException($"Material supplies {textures.Length} textures but technique '{technique}' samples {template.SampleCount}.");

		var sampler = template.Address switch
		{
			SamplerAddress.Repeat => template.LinearFilter ? _repeatLinearSampler : _repeatNearestSampler,
			SamplerAddress.Clamp => template.LinearFilter ? _linearSampler : _nearestSampler,
			_ => throw new VulkanException($"SamplerAddress '{template.Address}' is not mapped to a sampler."),
		};
		var set = _descriptors.Allocate(baked.DescriptorSetLayout);
		var count = template.SampleCount + (ubo.Present ? 1 : 0);
		var imageInfos = stackalloc DescriptorImageInfo[template.SampleCount];
		var bufferInfo = ubo.Info;
		var writes = stackalloc WriteDescriptorSet[count];
		for (var i = 0; i < template.SampleCount; i++)
		{
			imageInfos[i] = new DescriptorImageInfo
			{
				Sampler = sampler,
				ImageView = _textures.View(textures[i]),
				ImageLayout = ImageLayout.ShaderReadOnlyOptimal,
			};
			writes[i] = new WriteDescriptorSet
			{
				SType = StructureType.WriteDescriptorSet,
				DstSet = set,
				DstBinding = (uint)i,
				DescriptorCount = 1,
				DescriptorType = DescriptorType.CombinedImageSampler,
				PImageInfo = &imageInfos[i],
			};
		}
		if (ubo.Present)
			writes[template.SampleCount] = new WriteDescriptorSet
			{
				SType = StructureType.WriteDescriptorSet,
				DstSet = set,
				DstBinding = (uint)template.SampleCount,
				DescriptorCount = 1,
				DescriptorType = DescriptorType.UniformBuffer,
				PBufferInfo = &bufferInfo,
			};
		_vk.UpdateDescriptorSets(_ctx.Device, (uint)count, writes, 0, null);
		_vk.CmdBindDescriptorSets(_cmd, PipelineBindPoint.Graphics, baked.Layout, 0, 1, in set, 0, null);
	}

	// M15-0: a present per-frame uniform buffer to bind as a technique's last descriptor — the resolved
	// DescriptorBufferInfo plus a presence flag (default => absent). Copying Info to a local before taking its
	// address keeps the pointer valid through vkUpdateDescriptorSets.
	private readonly struct UniformBinding
	{
		public readonly DescriptorBufferInfo Info;
		public readonly bool Present;

		public UniformBinding(Silk.NET.Vulkan.Buffer buffer, ulong offset, ulong range)
		{
			Info = new DescriptorBufferInfo { Buffer = buffer, Offset = offset, Range = range };
			Present = true;
		}
	}

	// Stage a per-frame uniform block into the uniform ring and resolve it to a bindable descriptor (M15-0).
	private UniformBinding UploadUniforms(ReadOnlySpan<float> data)
	{
		var bytes = System.Runtime.InteropServices.MemoryMarshal.AsBytes(data);
		var (buffer, offset) = _uniformUpload.Upload(_frameSlot, bytes);
		return new UniformBinding(buffer, offset, (ulong)bytes.Length);
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

	// Nearest keeps the §9 pixel grid crisp (the upscale blit); linear gives bloom's downsample/upsample
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
	}
}
