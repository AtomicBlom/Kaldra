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
	private readonly Sampler _nearestSampler;

	private CommandBuffer _cmd;
	private VkFormat _colorFormat;

	public float DeltaTime { get; private set; }

	public RenderContext(
		VulkanContext ctx,
		TechniqueCache techniques,
		IReadOnlyDictionary<string, TechniqueTemplate> registry,
		TransientResources resources,
		DescriptorAllocator descriptors)
	{
		_ctx = ctx;
		_vk = ctx.Vk;
		_techniques = techniques;
		_registry = registry;
		_resources = resources;
		_descriptors = descriptors;
		_nearestSampler = CreateNearestSampler();
	}

	/// <summary>Bind the context to this frame's command buffer and timing before any pass records.</summary>
	public void BeginFrame(CommandBuffer cmd, float deltaTime)
	{
		_cmd = cmd;
		DeltaTime = deltaTime;
	}

	/// <summary>Set the colour format of the target the current pass writes — the format its techniques bake against.</summary>
	public void BeginPass(VkFormat colorFormat) => _colorFormat = colorFormat;

	public void FullScreen(string technique)
	{
		var baked = Bake(technique, samplesInput: false);
		_vk.CmdBindPipeline(_cmd, PipelineBindPoint.Graphics, baked.Pipeline);
		_vk.CmdDraw(_cmd, 3, 1, 0, 0); // 3 fabricated verts cover the target; no vertex buffer
	}

	public void FullScreen(string technique, ResourceHandle input)
	{
		ArgumentNullException.ThrowIfNull(input);
		var baked = Bake(technique, samplesInput: true);

		var set = _descriptors.Allocate(baked.DescriptorSetLayout);
		ref var image = ref _resources.Get(input.Id);
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

	private Technique Bake(string technique, bool samplesInput)
	{
		if (!_registry.TryGetValue(technique, out var template))
			throw new VulkanException($"No technique registered under the name '{technique}'.");
		return _techniques.GetOrBake(new TechniqueDescription(
			template.SpirvPath, template.VertexEntry, template.FragmentEntry, _colorFormat, samplesInput));
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
