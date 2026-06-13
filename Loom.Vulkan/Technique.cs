using Silk.NET.Vulkan;

namespace Loom.Vulkan;

/// <summary>
/// A baked graphics pipeline (the fluent describe-layer <c>TechniqueBuilder</c> arrives in M5). This is
/// the Bake layer in miniature: the whole <see cref="GraphicsPipelineCreateInfo"/> struct graph is
/// flattened transiently into a <see cref="NativeArena"/>, the pipeline is created (vkCreate* copies
/// everything), and the arena + shader module are freed immediately. Uses dynamic rendering — no
/// VkRenderPass.
/// </summary>
public sealed unsafe class Technique : IDisposable
{
	private readonly Vk _vk;
	private readonly Device _device;

	public Pipeline Pipeline { get; }
	public PipelineLayout Layout { get; }

	/// <summary>
	/// The descriptor set layout for a <see cref="TechniqueDescription.SamplesInput"/> technique (one
	/// combined image sampler at set 0/binding 0), or the null handle for a technique that samples nothing.
	/// </summary>
	public DescriptorSetLayout DescriptorSetLayout { get; }

	public Technique(VulkanContext ctx, in TechniqueDescription description, byte[] spirv)
	{
		_vk = ctx.Vk;
		_device = ctx.Device;

		var module = CreateShaderModule(spirv);
		try
		{
			DescriptorSetLayout = description.SamplesInput ? CreateSampledImageSetLayout() : default;
			Layout = CreatePipelineLayout(DescriptorSetLayout);
			Pipeline = CreatePipeline(
				module, description.VertexEntry, description.FragmentEntry, description.ColorFormat);
		}
		finally
		{
			// Pipeline creation has copied the bytecode; the module is no longer needed.
			_vk.DestroyShaderModule(_device, module, null);
		}
	}

	private ShaderModule CreateShaderModule(byte[] code)
	{
		fixed (byte* pCode = code)
		{
			ShaderModuleCreateInfo info = new()
			{
				SType = StructureType.ShaderModuleCreateInfo,
				CodeSize = (nuint)code.Length,
				PCode = (uint*)pCode,
			};
			_vk.CreateShaderModule(_device, in info, null, out var module).Check("vkCreateShaderModule");
			return module;
		}
	}

	// Set 0: a single combined image sampler at binding 0 (the .Samples input), visible to the fragment
	// shader. Hardcoded for M3's fullscreen-sample; SPIR-V reflection will derive bindings later.
	private DescriptorSetLayout CreateSampledImageSetLayout()
	{
		DescriptorSetLayoutBinding binding = new()
		{
			Binding = 0,
			DescriptorType = DescriptorType.CombinedImageSampler,
			DescriptorCount = 1,
			StageFlags = ShaderStageFlags.FragmentBit,
		};
		DescriptorSetLayoutCreateInfo info = new()
		{
			SType = StructureType.DescriptorSetLayoutCreateInfo,
			BindingCount = 1,
			PBindings = &binding,
		};
		_vk.CreateDescriptorSetLayout(_device, in info, null, out var layout).Check("vkCreateDescriptorSetLayout");
		return layout;
	}

	// No push constants yet. Zero or one descriptor set, depending on whether the technique samples.
	private PipelineLayout CreatePipelineLayout(DescriptorSetLayout setLayout)
	{
		var hasSet = setLayout.Handle != 0;
		PipelineLayoutCreateInfo info = new()
		{
			SType = StructureType.PipelineLayoutCreateInfo,
			SetLayoutCount = hasSet ? 1u : 0u,
			PSetLayouts = hasSet ? &setLayout : null,
		};
		_vk.CreatePipelineLayout(_device, in info, null, out var layout).Check("vkCreatePipelineLayout");
		return layout;
	}

	private Pipeline CreatePipeline(ShaderModule module, string vertexEntry, string fragmentEntry, Format colorFormat)
	{
		using var arena = new NativeArena();

		var stages = arena.Alloc<PipelineShaderStageCreateInfo>(2);
		stages[0] = new PipelineShaderStageCreateInfo
		{
			SType = StructureType.PipelineShaderStageCreateInfo,
			Stage = ShaderStageFlags.VertexBit,
			Module = module,
			PName = arena.String(vertexEntry),
		};
		stages[1] = new PipelineShaderStageCreateInfo
		{
			SType = StructureType.PipelineShaderStageCreateInfo,
			Stage = ShaderStageFlags.FragmentBit,
			Module = module,
			PName = arena.String(fragmentEntry),
		};

		// No vertex buffers: the fullscreen triangle is fabricated from SV_VertexID.
		var vertexInput = arena.Write(new PipelineVertexInputStateCreateInfo
		{
			SType = StructureType.PipelineVertexInputStateCreateInfo,
		});
		var inputAssembly = arena.Write(new PipelineInputAssemblyStateCreateInfo
		{
			SType = StructureType.PipelineInputAssemblyStateCreateInfo,
			Topology = PrimitiveTopology.TriangleList,
		});
		// Viewport/scissor are dynamic, so the pipeline survives swapchain resizes.
		var viewportState = arena.Write(new PipelineViewportStateCreateInfo
		{
			SType = StructureType.PipelineViewportStateCreateInfo,
			ViewportCount = 1,
			ScissorCount = 1,
		});
		var rasterization = arena.Write(new PipelineRasterizationStateCreateInfo
		{
			SType = StructureType.PipelineRasterizationStateCreateInfo,
			PolygonMode = PolygonMode.Fill,
			CullMode = CullModeFlags.None,
			FrontFace = FrontFace.CounterClockwise,
			LineWidth = 1.0f,
		});
		var multisample = arena.Write(new PipelineMultisampleStateCreateInfo
		{
			SType = StructureType.PipelineMultisampleStateCreateInfo,
			RasterizationSamples = SampleCountFlags.Count1Bit,
		});
		var blendAttachment = arena.Write(new PipelineColorBlendAttachmentState
		{
			BlendEnable = false,
			ColorWriteMask = ColorComponentFlags.RBit | ColorComponentFlags.GBit
				| ColorComponentFlags.BBit | ColorComponentFlags.ABit,
		});
		var colorBlend = arena.Write(new PipelineColorBlendStateCreateInfo
		{
			SType = StructureType.PipelineColorBlendStateCreateInfo,
			AttachmentCount = 1,
			PAttachments = blendAttachment,
		});
		var dynamicStates = arena.Write<DynamicState>([DynamicState.Viewport, DynamicState.Scissor]);
		var dynamicState = arena.Write(new PipelineDynamicStateCreateInfo
		{
			SType = StructureType.PipelineDynamicStateCreateInfo,
			DynamicStateCount = 2,
			PDynamicStates = dynamicStates,
		});

		// Dynamic rendering: the color attachment format(s) live here, chained via pNext.
		var colorFormatPtr = arena.Write(colorFormat);
		var renderingInfo = arena.Write(new PipelineRenderingCreateInfo
		{
			SType = StructureType.PipelineRenderingCreateInfo,
			ColorAttachmentCount = 1,
			PColorAttachmentFormats = colorFormatPtr,
		});

		GraphicsPipelineCreateInfo createInfo = new()
		{
			SType = StructureType.GraphicsPipelineCreateInfo,
			PNext = renderingInfo,
			StageCount = 2,
			PStages = stages,
			PVertexInputState = vertexInput,
			PInputAssemblyState = inputAssembly,
			PViewportState = viewportState,
			PRasterizationState = rasterization,
			PMultisampleState = multisample,
			PColorBlendState = colorBlend,
			PDynamicState = dynamicState,
			Layout = Layout,
			RenderPass = default, // dynamic rendering
			Subpass = 0,
		};

		_vk.CreateGraphicsPipelines(_device, default, 1, in createInfo, null, out var pipeline)
			.Check("vkCreateGraphicsPipelines");
		return pipeline;
	}

	public void Dispose()
	{
		if (Pipeline.Handle != 0)
			_vk.DestroyPipeline(_device, Pipeline, null);
		if (Layout.Handle != 0)
			_vk.DestroyPipelineLayout(_device, Layout, null);
		if (DescriptorSetLayout.Handle != 0)
			_vk.DestroyDescriptorSetLayout(_device, DescriptorSetLayout, null);
	}
}
