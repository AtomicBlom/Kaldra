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
	// Per-draw cache keyed by (name + attachment formats); keeps the hot path off TechniqueCache's
	// per-call SPIR-V file read. Misses fall through to TechniqueCache (which bakes + content-hashes once).
	private readonly Dictionary<string, Technique> _baked = [];

	private CommandBuffer _cmd;
	private VkFormat[] _colorFormats = [];
	private VkFormat _depthFormat;
	private bool _depthReadOnly;
	private int _frameSlot;
	private Camera _camera;

	public float DeltaTime { get; private set; }

	public Camera Camera => _camera;

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
		_nearestSampler = CreateNearestSampler();
	}

	/// <summary>Bind the context to this frame's command buffer, frame slot, camera, and timing before any pass records.</summary>
	public void BeginFrame(CommandBuffer cmd, float deltaTime, int frameSlot, Camera camera)
	{
		_cmd = cmd;
		DeltaTime = deltaTime;
		_frameSlot = frameSlot;
		_camera = camera;
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

	public void FullScreen(string technique, params ReadOnlySpan<ResourceHandle> inputs)
	{
		var baked = Bake(technique);
		_vk.CmdBindPipeline(_cmd, PipelineBindPoint.Graphics, baked.Pipeline);

		if (inputs.Length > 0)
		{
			// Bind each sampled input as a combined image sampler at bindings 0..N-1 (the graph put them in
			// ShaderReadOnly before this pass). One transient descriptor set, recycled with the frame slot.
			var set = _descriptors.Allocate(baked.DescriptorSetLayout);
			var imageInfos = stackalloc DescriptorImageInfo[inputs.Length];
			var writes = stackalloc WriteDescriptorSet[inputs.Length];
			for (var i = 0; i < inputs.Length; i++)
			{
				ref var image = ref _resources.Get(_frameSlot, inputs[i].Id);
				imageInfos[i] = new DescriptorImageInfo
				{
					Sampler = _nearestSampler,
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
			template.SampleCount, template.HasVertexInput, _depthFormat, template.Blend, DepthWrite: !_depthReadOnly));
		_baked[key] = baked;
		return baked;
	}

	private Sampler CreateNearestSampler()
	{
		SamplerCreateInfo info = new()
		{
			SType = StructureType.SamplerCreateInfo,
			MagFilter = Filter.Nearest,
			MinFilter = Filter.Nearest,
			MipmapMode = SamplerMipmapMode.Nearest,
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
	}
}
