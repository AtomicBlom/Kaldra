using System.Numerics;
using Loom.Core.Geometry;
using Loom.Core.Graph;
using Silk.NET.Vulkan;
using Format = Silk.NET.Vulkan.Format;
using VertexInputRate = Silk.NET.Vulkan.VertexInputRate;

namespace Loom.Vulkan;

/// <summary>
/// A baked graphics pipeline. This is the Bake layer in miniature: the whole
/// <see cref="GraphicsPipelineCreateInfo"/> struct graph is
/// flattened transiently into a <see cref="VulkanNativeArena"/>, the pipeline is created (vkCreate* copies
/// everything), and the arena + shader module are freed immediately. Uses dynamic rendering — no
/// VkRenderPass.
/// </summary>
public sealed unsafe class VulkanTechnique : IDisposable
{
	private readonly Vk _vk;
	private readonly Device _device;

	public Pipeline Pipeline { get; }
	public PipelineLayout Layout { get; }

	/// <summary>
	/// The descriptor set layout for a sampling technique (<see cref="VulkanTechniqueDescription.SampleCount"/>
	/// combined image samplers at set 0, bindings 0..N-1), or the null handle for a technique that samples nothing.
	/// </summary>
	public DescriptorSetLayout DescriptorSetLayout { get; }

	public VulkanTechnique(VulkanContext ctx, in VulkanTechniqueDescription description, byte[] spirv, string name)
	{
		_vk = ctx.Vk;
		_device = ctx.Device;

		var module = CreateShaderModule(spirv);
		// Name the module for GPU debuggers before it's destroyed below ([Conditional] — stripped without
		// UseNamedGPUObjects). The pipeline gets its own "pipeline:…" name once it's baked (VulkanRenderContext).
		ctx.SetObjectName(ObjectType.ShaderModule, module.Handle, $"shader:{name}");
		try
		{
			// A graphics technique's DESCRIPTORS are visible to BOTH vertex and fragment: most are sampled only in
			// the fragment stage, but a vertex-stage UBO (foliage wind) needs vertex visibility, and the broader
			// flag is harmless for the samplers. Compute techniques bind to the compute stage. The scalar
			// PUSH range, by contrast, stays single-stage (fragment for a draw, compute for a dispatch) — the
			// vkCmdPushConstants call pushes exactly that stage, so the range's flags must match it.
			var descriptorStage = description.IsCompute ? ShaderStageFlags.ComputeBit : ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit;
			var scalarPushStage = description.IsCompute ? ShaderStageFlags.ComputeBit : ShaderStageFlags.FragmentBit;
			DescriptorSetLayout = description.SampleCount + description.StorageCount + description.StorageBufferCount + description.UniformBufferCount > 0
				? CreateSetLayout(description.SampleCount, description.StorageCount, description.StorageBufferCount, description.UniformBufferCount, descriptorStage)
				: default;
			Layout = CreatePipelineLayout(DescriptorSetLayout, description.Layout is not null || description.Instance is not null, description.HasFragmentPush, scalarPushStage, description.HasListPush);
			Pipeline = description.IsCompute
				? CreateComputePipeline(module, in description)
				: CreatePipeline(module, in description);
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

	// Set 0, in binding order: <paramref name="sampleCount"/> combined image samplers (the .Samples inputs),
	// then <paramref name="storageImages"/> storage images (a compute's .ReadWrites of an image), then
	// <paramref name="storageBuffers"/> storage buffers (a compute's .ReadWrites of a buffer — GPU particles),
	// then <paramref name="uniformBuffers"/> uniform buffers (the per-frame uniform seam, fed from the
	// uniform ring), visible to the given stage. The shader reflection derives these counts; the
	// shaders declare the matching [[vk::binding(n,0)]] in the same category order.
	private DescriptorSetLayout CreateSetLayout(int sampleCount, int storageImages, int storageBuffers, int uniformBuffers, ShaderStageFlags stage)
	{
		var total = sampleCount + storageImages + storageBuffers + uniformBuffers;
		var bindings = stackalloc DescriptorSetLayoutBinding[total];
		for (var i = 0; i < total; i++)
			bindings[i] = new DescriptorSetLayoutBinding
			{
				Binding = (uint)i,
				DescriptorType = i < sampleCount ? DescriptorType.CombinedImageSampler
					: i < sampleCount + storageImages ? DescriptorType.StorageImage
					: i < sampleCount + storageImages + storageBuffers ? DescriptorType.StorageBuffer
					: DescriptorType.UniformBuffer,
				DescriptorCount = 1,
				StageFlags = stage,
			};
		DescriptorSetLayoutCreateInfo info = new()
		{
			SType = StructureType.DescriptorSetLayoutCreateInfo,
			BindingCount = (uint)total,
			PBindings = bindings,
		};
		_vk.CreateDescriptorSetLayout(_device, in info, null, out var layout).Check("vkCreateDescriptorSetLayout");
		return layout;
	}

	/// <summary>
	/// Size of the scalar push-constant block — up to sixteen floats. Small uses (JFA step, hovered id,
	/// exposure rate) fill the first few; the deferred-lighting shadow pass uses the full block for the sun's
	/// light-space matrix. Within the 128-byte minimum guarantee, and never coexists with the vertex MVP range.
	/// </summary>
	public const int FragmentPushSize = 16 * sizeof(float);

	/// <summary>
	/// Size of a draw-list technique's all-stage push block (<see cref="VulkanTechniqueDescription.HasListPush"/>):
	/// the per-list scalars plus the per-command tag slot the backend rewrites each draw. Eight 32-bit words covers
	/// the UI block (scale, translate, paper-white, hdr flag, kind) with headroom; well within the 128-byte minimum.
	/// </summary>
	public const int ListPushSize = 8 * sizeof(float);

	// Zero or one descriptor set (whether the technique binds resources), plus at most one push-constant
	// range: a draw-list technique gets an all-stage range for its scale/translate/tag block; mesh techniques
	// get a 64-byte VERTEX range for the MVP (small + per-draw, so push constants beat a uniform buffer);
	// post/compute techniques get a 16-byte range for per-pass scalars (JFA step, hover id, exposure rate) in
	// <paramref name="scalarStage"/> (fragment for a draw, compute for a dispatch). A technique uses exactly one
	// of these, so a single-element range array suffices.
	private PipelineLayout CreatePipelineLayout(DescriptorSetLayout setLayout, bool hasMvpPushConstant, bool hasScalarPush, ShaderStageFlags scalarStage, bool hasListPush)
	{
		var hasSet = setLayout.Handle != 0;
		var range = new PushConstantRange();
		if (hasListPush)
			range = new PushConstantRange { StageFlags = ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit, Offset = 0, Size = ListPushSize };
		else if (hasMvpPushConstant)
			range = new PushConstantRange { StageFlags = ShaderStageFlags.VertexBit, Offset = 0, Size = (uint)sizeof(Matrix4x4) };
		else if (hasScalarPush)
			range = new PushConstantRange { StageFlags = scalarStage, Offset = 0, Size = FragmentPushSize };
		var hasRange = hasMvpPushConstant || hasScalarPush || hasListPush;

		PipelineLayoutCreateInfo info = new()
		{
			SType = StructureType.PipelineLayoutCreateInfo,
			SetLayoutCount = hasSet ? 1u : 0u,
			PSetLayouts = hasSet ? &setLayout : null,
			PushConstantRangeCount = hasRange ? 1u : 0u,
			PPushConstantRanges = hasRange ? &range : null,
		};
		_vk.CreatePipelineLayout(_device, in info, null, out var layout).Check("vkCreatePipelineLayout");
		return layout;
	}

	// A compute pipeline is just the one compute stage + the layout — no fixed-function state, no render
	// target, no dynamic rendering. The entry point name rides in FragmentEntry (a compute technique has the
	// single entry there). This is the Bake layer's compute half: the auto-exposure reduction dispatches it.
	private Pipeline CreateComputePipeline(ShaderModule module, in VulkanTechniqueDescription description)
	{
		using var arena = new VulkanNativeArena();
		ComputePipelineCreateInfo createInfo = new()
		{
			SType = StructureType.ComputePipelineCreateInfo,
			Stage = new PipelineShaderStageCreateInfo
			{
				SType = StructureType.PipelineShaderStageCreateInfo,
				Stage = ShaderStageFlags.ComputeBit,
				Module = module,
				PName = arena.String(description.FragmentEntry),
			},
			Layout = Layout,
		};
		_vk.CreateComputePipelines(_device, default, 1, in createInfo, null, out var pipeline)
			.Check("vkCreateComputePipelines");
		return pipeline;
	}

	// Vertex input built from the technique's declared streams: an optional
	// per-vertex layout (e.g. the canonical Loom.Core.Vertex: Position/Normal/Uv/Tangent, 48 bytes) and an optional
	// per-instance layout (e.g. a 4×float4 world matrix for instanced drawing, or a GPU-particle stream). Each
	// stream's binding slot and advance rate come from the VertexLayout itself, so the per-instance stream sits at
	// binding 1 beside a mesh, or owns binding 0 outright when there is no per-vertex stream (a particle stream the
	// compute fills). Vulkan vertex-input locations are one flat namespace across bindings, so attributes get
	// sequential locations in stream order — per-vertex first (0..k-1), then per-instance (k..); the shader's
	// [[vk::location]]s must match.
	private static PipelineVertexInputStateCreateInfo* VertexInput(VulkanNativeArena arena, VertexLayout? vertexLayout, VertexLayout? instanceLayout)
	{
		List<VertexLayout> streams = [];
		if (vertexLayout is not null)
			streams.Add(vertexLayout);
		if (instanceLayout is not null)
			streams.Add(instanceLayout);

		var attributeCount = 0;
		foreach (var stream in streams)
			attributeCount += stream.Attributes.Length;
		var bindings = arena.Alloc<VertexInputBindingDescription>(streams.Count);
		var attributes = arena.Alloc<VertexInputAttributeDescription>(attributeCount);

		uint location = 0;
		var b = 0;
		var a = 0;
		foreach (var stream in streams)
		{
			var rate = stream.InputRate == Loom.Core.Geometry.VertexInputRate.PerInstance ? VertexInputRate.Instance : VertexInputRate.Vertex;
			bindings[b++] = new VertexInputBindingDescription { Binding = (uint)stream.Binding, Stride = (uint)stream.Stride, InputRate = rate };
			for (var i = 0; i < stream.Attributes.Length; i++)
				attributes[a++] = new VertexInputAttributeDescription { Location = location++, Binding = (uint)stream.Binding, Format = ToVkFormat(stream.Attributes[i].Format), Offset = (uint)stream.Offsets[i] };
		}

		return arena.Write(new PipelineVertexInputStateCreateInfo
		{
			SType = StructureType.PipelineVertexInputStateCreateInfo,
			VertexBindingDescriptionCount = (uint)streams.Count,
			PVertexBindingDescriptions = bindings,
			VertexAttributeDescriptionCount = (uint)attributeCount,
			PVertexAttributeDescriptions = attributes,
		});
	}

	private static Format ToVkFormat(VertexFormat format) => format switch
	{
		VertexFormat.Float => Format.R32Sfloat,
		VertexFormat.Float2 => Format.R32G32Sfloat,
		VertexFormat.Float3 => Format.R32G32B32Sfloat,
		VertexFormat.Float4 => Format.R32G32B32A32Sfloat,
		VertexFormat.UInt4 => Format.R32G32B32A32Uint,
		VertexFormat.Half4 => Format.R16G16B16A16Sfloat,
		VertexFormat.UNorm4 => Format.R8G8B8A8Unorm,
		_ => throw new ArgumentOutOfRangeException(nameof(format), format, "Unknown vertex format."),
	};

	private Pipeline CreatePipeline(ShaderModule module, in VulkanTechniqueDescription description)
	{
		using var arena = new VulkanNativeArena();

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

		var vertexInput = description.Layout is not null || description.Instance is not null
			? VertexInput(arena, description.Layout, description.Instance)
			// No vertex buffers: the fullscreen triangle is fabricated from SV_VertexID.
			: arena.Write(new PipelineVertexInputStateCreateInfo { SType = StructureType.PipelineVertexInputStateCreateInfo });
		var inputAssembly = arena.Write(new PipelineInputAssemblyStateCreateInfo
		{
			SType = StructureType.PipelineInputAssemblyStateCreateInfo,
			Topology = PrimitiveTopology.TriangleList, // particles are instanced quads, not point sprites
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
			CullMode = description.Cull switch
			{
				CullMode.None => CullModeFlags.None,
				CullMode.Back => CullModeFlags.BackBit,
				CullMode.Front => CullModeFlags.FrontBit,
				_ => throw new NotSupportedException($"Vulkan: CullMode '{description.Cull}' is not mapped to cull flags."),
			},
			// Counter-clockwise winding is the front face (the glTF convention: triangles wound CCW when viewed from
			// outside face the camera), so Cull.Back discards the away-facing triangles. The DX12 backend uses the
			// same convention, so a technique's CullMode discards the same triangles on either backend.
			FrontFace = FrontFace.CounterClockwise,
			LineWidth = 1.0f,
		});
		var multisample = arena.Write(new PipelineMultisampleStateCreateInfo
		{
			SType = StructureType.PipelineMultisampleStateCreateInfo,
			RasterizationSamples = SampleCountFlags.Count1Bit,
		});
		// One blend attachment per colour target (a g-buffer writes several). The blend equation is technique
		// state: None writes opaquely, AlphaOver does src-alpha over-blend (transparency),
		// Additive sums src+dst (bloom accumulation). All pair with a pass that .Blends the target (LoadOp.Load).
		var (srcFactor, dstFactor) = description.Blend switch
		{
			BlendMode.AlphaOver => (BlendFactor.SrcAlpha, BlendFactor.OneMinusSrcAlpha),
			BlendMode.PremultipliedAlpha => (BlendFactor.One, BlendFactor.OneMinusSrcAlpha),
			BlendMode.Additive => (BlendFactor.One, BlendFactor.One),
			BlendMode.Multiply => (BlendFactor.DstColor, BlendFactor.Zero),
			BlendMode.None => (BlendFactor.One, BlendFactor.Zero),
			_ => throw new NotSupportedException($"Vulkan: BlendMode '{description.Blend}' is not mapped to blend factors."),
		};
		var colorCount = description.ColorFormats.Length;
		var blendAttachments = arena.Alloc<PipelineColorBlendAttachmentState>(colorCount);
		for (var i = 0; i < colorCount; i++)
			blendAttachments[i] = new PipelineColorBlendAttachmentState
			{
				BlendEnable = description.Blend != BlendMode.None,
				SrcColorBlendFactor = srcFactor,
				DstColorBlendFactor = dstFactor,
				ColorBlendOp = BlendOp.Add,
				SrcAlphaBlendFactor = srcFactor,
				DstAlphaBlendFactor = dstFactor,
				AlphaBlendOp = BlendOp.Add,
				ColorWriteMask = ColorComponentFlags.RBit | ColorComponentFlags.GBit
					| ColorComponentFlags.BBit | ColorComponentFlags.ABit,
			};
		var colorBlend = arena.Write(new PipelineColorBlendStateCreateInfo
		{
			SType = StructureType.PipelineColorBlendStateCreateInfo,
			AttachmentCount = (uint)colorCount,
			PAttachments = blendAttachments,
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
				DepthWriteEnable = description.DepthWrite,
				DepthCompareOp = CompareOp.LessOrEqual,
			})
			: null;

		// Dynamic rendering: the attachment format(s) live here, chained via pNext.
		var colorFormats = arena.Write((ReadOnlySpan<Format>)description.ColorFormats);
		var renderingInfo = arena.Write(new PipelineRenderingCreateInfo
		{
			SType = StructureType.PipelineRenderingCreateInfo,
			ColorAttachmentCount = (uint)colorCount,
			PColorAttachmentFormats = colorFormats,
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
