using System.Numerics;

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
			Layout = CreatePipelineLayout(DescriptorSetLayout, description.HasVertexInput);
			Pipeline = CreatePipeline(module, in description);
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

	// Zero or one descriptor set (whether the technique samples). Mesh techniques also get a 64-byte vertex
	// push constant for the MVP matrix — small and per-draw, so push constants beat a uniform buffer here.
	private PipelineLayout CreatePipelineLayout(DescriptorSetLayout setLayout, bool hasMvpPushConstant)
	{
		var hasSet = setLayout.Handle != 0;
		PushConstantRange mvp = new()
		{
			StageFlags = ShaderStageFlags.VertexBit,
			Offset = 0,
			Size = (uint)sizeof(Matrix4x4),
		};
		PipelineLayoutCreateInfo info = new()
		{
			SType = StructureType.PipelineLayoutCreateInfo,
			SetLayoutCount = hasSet ? 1u : 0u,
			PSetLayouts = hasSet ? &setLayout : null,
			PushConstantRangeCount = hasMvpPushConstant ? 1u : 0u,
			PPushConstantRanges = hasMvpPushConstant ? &mvp : null,
		};
		_vk.CreatePipelineLayout(_device, in info, null, out var layout).Check("vkCreatePipelineLayout");
		return layout;
	}

	// Vertex input for the one canonical interleaved layout (Loom.Core.Vertex: Vector3 Position, Vector3
	// Normal, Vector2 Uv — sequential, 32 bytes). Offsets mirror that field order. Per-technique layouts
	// (reflected or declared) can come later.
	private static PipelineVertexInputStateCreateInfo* VertexInputForMesh(NativeArena arena)
	{
		var binding = arena.Write(new VertexInputBindingDescription
		{
			Binding = 0,
			Stride = (uint)sizeof(Loom.Core.Vertex),
			InputRate = VertexInputRate.Vertex,
		});

		var attributes = arena.Alloc<VertexInputAttributeDescription>(3);
		attributes[0] = new VertexInputAttributeDescription { Location = 0, Binding = 0, Format = Format.R32G32B32Sfloat, Offset = 0 };  // Position
		attributes[1] = new VertexInputAttributeDescription { Location = 1, Binding = 0, Format = Format.R32G32B32Sfloat, Offset = 12 }; // Normal
		attributes[2] = new VertexInputAttributeDescription { Location = 2, Binding = 0, Format = Format.R32G32Sfloat, Offset = 24 };    // Uv

		return arena.Write(new PipelineVertexInputStateCreateInfo
		{
			SType = StructureType.PipelineVertexInputStateCreateInfo,
			VertexBindingDescriptionCount = 1,
			PVertexBindingDescriptions = binding,
			VertexAttributeDescriptionCount = 3,
			PVertexAttributeDescriptions = attributes,
		});
	}

	private Pipeline CreatePipeline(ShaderModule module, in TechniqueDescription description)
	{
		using var arena = new NativeArena();

		var stages = arena.Alloc<PipelineShaderStageCreateInfo>(2);
		stages[0] = new PipelineShaderStageCreateInfo
		{
			SType = StructureType.PipelineShaderStageCreateInfo,
			Stage = ShaderStageFlags.VertexBit,
			Module = module,
			PName = arena.String(description.VertexEntry),
		};
		stages[1] = new PipelineShaderStageCreateInfo
		{
			SType = StructureType.PipelineShaderStageCreateInfo,
			Stage = ShaderStageFlags.FragmentBit,
			Module = module,
			PName = arena.String(description.FragmentEntry),
		};

		var vertexInput = description.HasVertexInput
			? VertexInputForMesh(arena)
			// No vertex buffers: the fullscreen triangle is fabricated from SV_VertexID.
			: arena.Write(new PipelineVertexInputStateCreateInfo { SType = StructureType.PipelineVertexInputStateCreateInfo });
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

		// Depth: enabled when the pass writes a depth attachment (the backend supplies its format). Standard
		// reverse-less-than test with write; no stencil. Null when the technique has no depth target.
		var hasDepth = description.DepthFormat != Format.Undefined;
		var depthStencil = hasDepth
			? arena.Write(new PipelineDepthStencilStateCreateInfo
			{
				SType = StructureType.PipelineDepthStencilStateCreateInfo,
				DepthTestEnable = true,
				DepthWriteEnable = true,
				DepthCompareOp = CompareOp.LessOrEqual,
			})
			: null;

		// Dynamic rendering: the attachment format(s) live here, chained via pNext.
		var colorFormatPtr = arena.Write(description.ColorFormat);
		var renderingInfo = arena.Write(new PipelineRenderingCreateInfo
		{
			SType = StructureType.PipelineRenderingCreateInfo,
			ColorAttachmentCount = 1,
			PColorAttachmentFormats = colorFormatPtr,
			DepthAttachmentFormat = hasDepth ? description.DepthFormat : Format.Undefined,
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
			PDepthStencilState = depthStencil,
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
