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

	private CommandBuffer _cmd;
	private VkFormat _colorFormat;
	private VkFormat _depthFormat;
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

	/// <summary>Set the attachment formats of the current pass — the formats its techniques bake against.</summary>
	public void BeginPass(VkFormat colorFormat, VkFormat depthFormat)
	{
		_colorFormat = colorFormat;
		_depthFormat = depthFormat;
	}

	public void FullScreen(string technique)
	{
		var baked = Bake(technique);
		_vk.CmdBindPipeline(_cmd, PipelineBindPoint.Graphics, baked.Pipeline);
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

	public void FullScreen(string technique, ResourceHandle input)
	{
		ArgumentNullException.ThrowIfNull(input);
		var baked = Bake(technique);

		var set = _descriptors.Allocate(baked.DescriptorSetLayout);
		ref var image = ref _resources.Get(_frameSlot, input.Id);
		DescriptorImageInfo imageInfo = new()
		{
			Sampler = _nearestSampler,
			ImageView = image.View,
			ImageLayout = ImageLayout.ShaderReadOnlyOptimal, // the graph transitioned it before this pass
		};
		WriteDescriptorSet write = new()
		{
			SType = StructureType.WriteDescriptorSet,
			DstSet = set,
			DstBinding = 0,
			DescriptorCount = 1,
			DescriptorType = DescriptorType.CombinedImageSampler,
			PImageInfo = &imageInfo,
		};
		_vk.UpdateDescriptorSets(_ctx.Device, 1, in write, 0, null);

		_vk.CmdBindPipeline(_cmd, PipelineBindPoint.Graphics, baked.Pipeline);
		_vk.CmdBindDescriptorSets(_cmd, PipelineBindPoint.Graphics, baked.Layout, 0, 1, in set, 0, null);
		_vk.CmdDraw(_cmd, 3, 1, 0, 0);
	}

	// The technique's shape (samples an input? reads a vertex buffer?) comes from its registered template;
	// the colour format is the current pass's target. So one template baked against two formats caches two
	// pipelines, and the call site only needs the name.
	private Technique Bake(string technique)
	{
		if (!_registry.TryGetValue(technique, out var template))
			throw new VulkanException($"No technique registered under the name '{technique}'.");
		return _techniques.GetOrBake(new TechniqueDescription(
			template.SpirvPath, template.VertexEntry, template.FragmentEntry, _colorFormat,
			template.SamplesInput, template.HasVertexInput, _depthFormat));
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
