using System.Numerics;

using Loom.Core;
using Silk.NET.Vulkan;
using VkFormat = Silk.NET.Vulkan.Format;

namespace Loom.Vulkan;

/// <summary>
/// The backend's <see cref="IRenderContext"/> — what a pass's <c>.Draw</c> callback issues GPU work
/// through. It is a thin recorder over the frame's command buffer: it resolves a technique name to a baked
/// pipeline (baked against the current pass's target format), wires up any sampled input as a descriptor
/// set, and emits the draw. State that changes per frame/pass (the command buffer, the target format) is
/// pushed in by the <see cref="GraphExecutor"/> via <see cref="BeginFrame"/>/<see cref="BeginPass"/> so the
/// callbacks stay the pure, capture-free closures the design wants (Planning.md §8).
/// </summary>
internal sealed unsafe class RenderContext : IRenderContext, IDisposable
{
	private readonly VulkanContext _ctx;
	private readonly Vk _vk;
	private readonly TechniqueCache _techniques;
	private readonly IReadOnlyDictionary<string, TechniqueTemplate> _registry;
	private readonly TransientResources _resources;
	private readonly DescriptorAllocator _descriptors;
	private readonly MeshStore _meshes;
	private readonly Sampler _nearestSampler;
	private readonly Sampler _linearSampler;
	// Per-draw cache keyed by (name + attachment formats); keeps the hot path off TechniqueCache's
	// per-call SPIR-V file read. Misses fall through to TechniqueCache (which bakes + content-hashes once).
	private readonly Dictionary<string, Technique> _baked = [];

	private CommandBuffer _cmd;
	private VkFormat[] _colorFormats = [];
	private VkFormat _depthFormat;
	private bool _depthReadOnly;
	private int _frameSlot;
	private Camera _camera;
	private uint _hoverId;

	public float DeltaTime { get; private set; }

	public Camera Camera => _camera;

	public uint HoverId => _hoverId;

	public RenderContext(
		VulkanContext ctx,
		TechniqueCache techniques,
		IReadOnlyDictionary<string, TechniqueTemplate> registry,
		TransientResources resources,
		DescriptorAllocator descriptors,
		MeshStore meshes)
	{
		_ctx = ctx;
		_vk = ctx.Vk;
		_techniques = techniques;
		_registry = registry;
		_resources = resources;
		_descriptors = descriptors;
		_meshes = meshes;
		_nearestSampler = CreateSampler(Filter.Nearest);
		_linearSampler = CreateSampler(Filter.Linear);
	}

	/// <summary>Bind the context to this frame's command buffer, frame slot, camera, hover, and timing before any pass records.</summary>
	public void BeginFrame(CommandBuffer cmd, float deltaTime, int frameSlot, Camera camera, uint hoverId)
	{
		_cmd = cmd;
		DeltaTime = deltaTime;
		_frameSlot = frameSlot;
		_camera = camera;
		_hoverId = hoverId;
	}

	/// <summary>
	/// Set the attachment formats of the current pass — the formats its techniques bake against — plus whether
	/// the depth attachment is bound read-only (<c>.ReadsDepth</c>): a read-only pass bakes with depth writes
	/// disabled so a transparent draw tests against, but does not disturb, the opaque depth.
	/// </summary>
	public void BeginPass(ReadOnlySpan<VkFormat> colorFormats, VkFormat depthFormat, bool depthReadOnly)
	{
		_colorFormats = colorFormats.ToArray();
		_depthFormat = depthFormat;
		_depthReadOnly = depthReadOnly;
	}

	public void FullScreen(string technique, params ReadOnlySpan<ResourceHandle> inputs) =>
		FullScreen(technique, default, inputs);

	public void FullScreen(string technique, ReadOnlySpan<float> push, params ReadOnlySpan<ResourceHandle> inputs)
	{
		var baked = Bake(technique);
		_vk.CmdBindPipeline(_cmd, PipelineBindPoint.Graphics, baked.Pipeline);

		if (push.Length > 0)
		{
			// The fragment scalar channel: pad to the layout's fixed range so the shader's float4 always reads
			// defined memory, then push to the fragment stage at offset 0.
			var block = stackalloc float[4];
			for (var i = 0; i < push.Length && i < 4; i++)
				block[i] = push[i];
			_vk.CmdPushConstants(_cmd, baked.Layout, ShaderStageFlags.FragmentBit, 0, (uint)Technique.FragmentPushSize, block);
		}

		if (inputs.Length > 0)
		{
			// Bind each sampled input as a combined image sampler at bindings 0..N-1 (the graph put them in
			// ShaderReadOnly before this pass). One transient descriptor set, recycled with the frame slot.
			var sampler = _registry[technique].LinearFilter ? _linearSampler : _nearestSampler;
			var set = _descriptors.Allocate(baked.DescriptorSetLayout);
			var imageInfos = stackalloc DescriptorImageInfo[inputs.Length];
			var writes = stackalloc WriteDescriptorSet[inputs.Length];
			for (var i = 0; i < inputs.Length; i++)
			{
				ref var image = ref _resources.Get(_frameSlot, inputs[i].Id);
				imageInfos[i] = new DescriptorImageInfo
				{
					Sampler = sampler,
					ImageView = image.View,
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
			_vk.UpdateDescriptorSets(_ctx.Device, (uint)inputs.Length, writes, 0, null);
			_vk.CmdBindDescriptorSets(_cmd, PipelineBindPoint.Graphics, baked.Layout, 0, 1, in set, 0, null);
		}

		_vk.CmdDraw(_cmd, 3, 1, 0, 0); // 3 fabricated verts cover the target; no vertex buffer
	}

	public void Draw(Mesh mesh, string technique)
	{
		var baked = Bake(technique);
		var mvp = _camera.ViewProjection; // model is identity for now; per-object transforms come later
		_vk.CmdBindPipeline(_cmd, PipelineBindPoint.Graphics, baked.Pipeline);
		_vk.CmdPushConstants(_cmd, baked.Layout, ShaderStageFlags.VertexBit, 0, (uint)sizeof(Matrix4x4), &mvp);
		_meshes.Draw(_cmd, mesh.Id); // binds the mesh's vertex/index buffers and records the indexed draw
	}

	public void Dispatch(string technique, (uint X, uint Y, uint Z) groups, ReadOnlySpan<float> push, ReadOnlySpan<ResourceHandle> sampled, ReadOnlySpan<ResourceHandle> storage)
	{
		var baked = Bake(technique);
		_vk.CmdBindPipeline(_cmd, PipelineBindPoint.Compute, baked.Pipeline);

		if (push.Length > 0)
		{
			var block = stackalloc float[4];
			for (var i = 0; i < push.Length && i < 4; i++)
				block[i] = push[i];
			_vk.CmdPushConstants(_cmd, baked.Layout, ShaderStageFlags.ComputeBit, 0, (uint)Technique.FragmentPushSize, block);
		}

		var total = sampled.Length + storage.Length;
		if (total > 0)
		{
			// Bindings 0..S-1: sampled inputs (ShaderReadOnly, the nearest sampler). S..S+T-1: storage images
			// (GENERAL) the compute read-writes. The graph put each in the matching layout before this pass.
			var set = _descriptors.Allocate(baked.DescriptorSetLayout);
			var imageInfos = stackalloc DescriptorImageInfo[total];
			var writes = stackalloc WriteDescriptorSet[total];
			for (var i = 0; i < total; i++)
			{
				var isSampled = i < sampled.Length;
				var handle = isSampled ? sampled[i] : storage[i - sampled.Length];
				ref var image = ref _resources.Get(_frameSlot, handle.Id);
				imageInfos[i] = new DescriptorImageInfo
				{
					Sampler = isSampled ? _nearestSampler : default,
					ImageView = image.View,
					ImageLayout = isSampled ? ImageLayout.ShaderReadOnlyOptimal : ImageLayout.General,
				};
				writes[i] = new WriteDescriptorSet
				{
					SType = StructureType.WriteDescriptorSet,
					DstSet = set,
					DstBinding = (uint)i,
					DescriptorCount = 1,
					DescriptorType = isSampled ? DescriptorType.CombinedImageSampler : DescriptorType.StorageImage,
					PImageInfo = &imageInfos[i],
				};
			}
			_vk.UpdateDescriptorSets(_ctx.Device, (uint)total, writes, 0, null);
			_vk.CmdBindDescriptorSets(_cmd, PipelineBindPoint.Compute, baked.Layout, 0, 1, in set, 0, null);
		}

		_vk.CmdDispatch(_cmd, groups.X, groups.Y, groups.Z);
	}

	// The technique's shape (samples an input? reads a vertex buffer?) comes from its registered template;
	// the attachment formats are the current pass's. So one template used in passes with different targets
	// caches distinct pipelines, and the call site only needs the name. The local cache keeps the per-draw
	// path off TechniqueCache's file read.
	private Technique Bake(string technique)
	{
		if (!_registry.TryGetValue(technique, out var template))
			throw new VulkanException($"No technique registered under the name '{technique}'.");

		var key = $"{technique}|{string.Join(',', _colorFormats)}|{_depthFormat}|{_depthReadOnly}";
		if (_baked.TryGetValue(key, out var cached))
			return cached;

		// A pass that reads depth read-only (.ReadsDepth) bakes with depth writes off; one that writes it
		// (.WritesDepth, or no depth at all) keeps writes on.
		var baked = _techniques.GetOrBake(new TechniqueDescription(
			template.SpirvPath, template.VertexEntry, template.FragmentEntry, _colorFormats,
			template.SampleCount, template.HasVertexInput, _depthFormat, template.Blend, DepthWrite: !_depthReadOnly,
			HasFragmentPush: template.HasFragmentPush, IsCompute: template.IsCompute, StorageCount: template.StorageCount));
		// Note: template.Blend / LinearFilter / HasFragmentPush need no extra key term — Blend and
		// HasFragmentPush are folded into the TechniqueDescription hash, and LinearFilter only affects the
		// descriptor's sampler, not the pipeline.
		_baked[key] = baked;
		return baked;
	}

	// Nearest keeps the §9 pixel grid crisp (the upscale blit); linear gives bloom's downsample/upsample
	// the smooth filtering a chunky pixel glow still wants internally. The sampler is part of the combined
	// image-sampler descriptor, so the technique's LinearFilter flag just selects which one FullScreen binds.
	private Sampler CreateSampler(Filter filter)
	{
		var mip = filter == Filter.Linear ? SamplerMipmapMode.Linear : SamplerMipmapMode.Nearest;
		SamplerCreateInfo info = new()
		{
			SType = StructureType.SamplerCreateInfo,
			MagFilter = filter,
			MinFilter = filter,
			MipmapMode = mip,
			AddressModeU = SamplerAddressMode.ClampToEdge,
			AddressModeV = SamplerAddressMode.ClampToEdge,
			AddressModeW = SamplerAddressMode.ClampToEdge,
			MaxLod = Vk.LodClampNone,
		};
		_vk.CreateSampler(_ctx.Device, in info, null, out var sampler).Check("vkCreateSampler");
		return sampler;
	}

	public void Dispose()
	{
		if (_nearestSampler.Handle != 0)
			_vk.DestroySampler(_ctx.Device, _nearestSampler, null);
		if (_linearSampler.Handle != 0)
			_vk.DestroySampler(_ctx.Device, _linearSampler, null);
	}
}
