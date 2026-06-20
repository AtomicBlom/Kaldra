using System.Runtime.InteropServices;

using Loom.Core;
using Loom.Core.Materials;
using Loom.Core.UI;
using Silk.NET.Vulkan;
using VMASharp;

using Buffer = Silk.NET.Vulkan.Buffer;
using VkFormat = Silk.NET.Vulkan.Format;

namespace Loom.Vulkan;

/// <summary>
/// The backend's game-UI renderer (PlanningUI.md §D6) — the twin of <see cref="ImGuiRenderer"/> for the XAML UI
/// engine, cloned from it. The "ui" pass's <c>ctx.DrawUI(surface)</c> forwards that surface's composited, neutral
/// <see cref="UIDrawData"/> here; this records it into the already-open dynamic-rendering scope (the pass
/// <c>.Blends</c> the backbuffer at native resolution). It never begins/ends rendering itself.
/// <para>
/// Differences from the ImGui twin: the vertex layout is <see cref="UIVertex"/> (premultiplied RGBA16F colour +
/// per-prim params), so the pipeline blends <b>premultiplied</b> alpha-over; one <c>ui.slang</c> pipeline draws
/// every primitive family, the <see cref="UIDrawCommand.Kind"/> selected per command via a push constant; and a
/// small texture table (a 1×1 white texel at <see cref="UIDrawCommand.TextureId"/> 0 for fills/rects, the MTSDF
/// font atlas at 1) is bound per command. Vertex/index buffers are host-visible and grown per frame-in-flight slot.
/// </para>
/// </summary>
internal sealed unsafe class UIRenderer : IDisposable
{
	private readonly VulkanContext _ctx;
	private readonly Vk _vk;
	private readonly VulkanMemoryAllocator _allocator;

	private Pipeline _pipeline;
	private PipelineLayout _layout;
	private DescriptorSetLayout _setLayout;
	private DescriptorPool _descriptorPool;
	private Sampler _sampler;

	// Texture table: index = UIDrawCommand.TextureId. 0 = a 1×1 white texel (solid fills/rects); 1 = the MTSDF
	// font atlas page (glyphs). Each is a device-local image + view + a persistent combined-image-sampler set.
	private readonly Image[] _texImages;
	private readonly ImageView[] _texViews;
	private readonly Allocation?[] _texAllocs;
	private readonly DescriptorSet[] _texSets;
	private readonly int _texCount;

	// Per frame-in-flight slot: host-visible vertex + index buffers, grown on demand (UI geometry varies frame to
	// frame). Multi-buffered so a frame's upload can't clobber a still-in-flight earlier frame of the same slot.
	private readonly int _framesInFlight;
	private readonly Buffer[] _vtxBuffers;
	private readonly Allocation?[] _vtxAllocations;
	private readonly ulong[] _vtxSizes;
	private readonly Buffer[] _idxBuffers;
	private readonly Allocation?[] _idxAllocations;
	private readonly ulong[] _idxSizes;

	// Push block (ui.slang PushConstants): scale.xy, translate.xy, kind (uint), paperWhite, isHdr (uint) = 28 bytes.
	private const uint PushSize = 7 * sizeof(float);
	// Paper-white luminance reference for an HDR surface: UIPaperWhiteNits / 80 (§D4.4). SDR surfaces pass 1.0.
	private const float HdrPaperWhiteScale = 200f / 80f;

	public UIRenderer(VulkanContext ctx, VulkanMemoryAllocator allocator, CommandPool commandPool, VkFormat colorFormat, byte[] spirv, int framesInFlight, TextureData? fontAtlas)
	{
		_ctx = ctx;
		_vk = ctx.Vk;
		_allocator = allocator;
		_framesInFlight = framesInFlight;
		_vtxBuffers = new Buffer[framesInFlight];
		_vtxAllocations = new Allocation?[framesInFlight];
		_vtxSizes = new ulong[framesInFlight];
		_idxBuffers = new Buffer[framesInFlight];
		_idxAllocations = new Allocation?[framesInFlight];
		_idxSizes = new ulong[framesInFlight];

		_texCount = fontAtlas is null ? 1 : 2;
		_texImages = new Image[_texCount];
		_texViews = new ImageView[_texCount];
		_texAllocs = new Allocation?[_texCount];
		_texSets = new DescriptorSet[_texCount];

		CreateSampler();
		CreateDescriptors(_texCount);
		CreatePipeline(colorFormat, spirv);

		// Texture 0: a 1×1 white texel — solid fills/rounded-rects/borders sample it (analytic SDF folds coverage).
		UploadTexture(0, 1, 1, [255, 255, 255, 255], commandPool);
		// Texture 1: the MTSDF font atlas (linear/UNORM — SDF distances are not sRGB). MU1 uploads level 0 only.
		if (fontAtlas is not null)
		{
			var level = fontAtlas.Levels[0];
			UploadTexture(1, level.Width, level.Height, level.Pixels, commandPool);
		}
	}

	private void CreateSampler()
	{
		SamplerCreateInfo info = new()
		{
			SType = StructureType.SamplerCreateInfo,
			MagFilter = Filter.Linear,
			MinFilter = Filter.Linear,
			MipmapMode = SamplerMipmapMode.Linear,
			AddressModeU = SamplerAddressMode.ClampToEdge,
			AddressModeV = SamplerAddressMode.ClampToEdge,
			AddressModeW = SamplerAddressMode.ClampToEdge,
			MinLod = -1000,
			MaxLod = 1000,
		};
		_vk.CreateSampler(_ctx.Device, in info, null, out _sampler).Check("vkCreateSampler");
	}

	// One combined-image-sampler binding, with one persistent set per texture-table entry (bound per command by
	// UIDrawCommand.TextureId), mirroring ImGui's per-command texture bind.
	private void CreateDescriptors(int count)
	{
		DescriptorSetLayoutBinding binding = new()
		{
			Binding = 0,
			DescriptorType = DescriptorType.CombinedImageSampler,
			DescriptorCount = 1,
			StageFlags = ShaderStageFlags.FragmentBit,
		};
		DescriptorSetLayoutCreateInfo layoutInfo = new()
		{
			SType = StructureType.DescriptorSetLayoutCreateInfo,
			BindingCount = 1,
			PBindings = &binding,
		};
		_vk.CreateDescriptorSetLayout(_ctx.Device, in layoutInfo, null, out _setLayout).Check("vkCreateDescriptorSetLayout");

		DescriptorPoolSize poolSize = new() { Type = DescriptorType.CombinedImageSampler, DescriptorCount = (uint)count };
		DescriptorPoolCreateInfo poolInfo = new()
		{
			SType = StructureType.DescriptorPoolCreateInfo,
			MaxSets = (uint)count,
			PoolSizeCount = 1,
			PPoolSizes = &poolSize,
		};
		_vk.CreateDescriptorPool(_ctx.Device, in poolInfo, null, out _descriptorPool).Check("vkCreateDescriptorPool");

		for (var i = 0; i < count; i++)
		{
			var setLayout = _setLayout;
			DescriptorSetAllocateInfo allocInfo = new()
			{
				SType = StructureType.DescriptorSetAllocateInfo,
				DescriptorPool = _descriptorPool,
				DescriptorSetCount = 1,
				PSetLayouts = &setLayout,
			};
			_vk.AllocateDescriptorSets(_ctx.Device, in allocInfo, out _texSets[i]).Check("vkAllocateDescriptorSets");
		}
	}

	// The ui.slang pipeline: UIVertex layout, PREMULTIPLIED alpha-over blend, no depth, dynamic viewport/scissor,
	// baked against the target colour format via dynamic rendering. Mirrors ImGuiRenderer.CreatePipeline.
	private void CreatePipeline(VkFormat colorFormat, byte[] spirv)
	{
		var module = CreateShaderModule(spirv);
		_ctx.SetObjectName(ObjectType.ShaderModule, module.Handle, "shader:ui");
		try
		{
			var setLayout = _setLayout;
			PushConstantRange pushRange = new() { StageFlags = ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit, Offset = 0, Size = PushSize };
			PipelineLayoutCreateInfo layoutInfo = new()
			{
				SType = StructureType.PipelineLayoutCreateInfo,
				SetLayoutCount = 1,
				PSetLayouts = &setLayout,
				PushConstantRangeCount = 1,
				PPushConstantRanges = &pushRange,
			};
			_vk.CreatePipelineLayout(_ctx.Device, in layoutInfo, null, out _layout).Check("vkCreatePipelineLayout");

			using var arena = new NativeArena();

			var stages = arena.Alloc<PipelineShaderStageCreateInfo>(2);
			stages[0] = new PipelineShaderStageCreateInfo
			{
				SType = StructureType.PipelineShaderStageCreateInfo,
				Stage = ShaderStageFlags.VertexBit,
				Module = module,
				PName = arena.String("vertexMain"),
			};
			stages[1] = new PipelineShaderStageCreateInfo
			{
				SType = StructureType.PipelineShaderStageCreateInfo,
				Stage = ShaderStageFlags.FragmentBit,
				Module = module,
				PName = arena.String("fragmentMain"),
			};

			// UIVertex: position (float2 @0), uv (float2 @8), colour (half4 @16), params (float4 @24) — stride 40.
			var binding = arena.Write(new VertexInputBindingDescription
			{
				Binding = 0,
				Stride = (uint)sizeof(UIVertex),
				InputRate = Silk.NET.Vulkan.VertexInputRate.Vertex,
			});
			var attributes = arena.Alloc<VertexInputAttributeDescription>(4);
			attributes[0] = new VertexInputAttributeDescription { Location = 0, Binding = 0, Format = VkFormat.R32G32Sfloat, Offset = 0 };
			attributes[1] = new VertexInputAttributeDescription { Location = 1, Binding = 0, Format = VkFormat.R32G32Sfloat, Offset = 8 };
			attributes[2] = new VertexInputAttributeDescription { Location = 2, Binding = 0, Format = VkFormat.R16G16B16A16Sfloat, Offset = 16 };
			attributes[3] = new VertexInputAttributeDescription { Location = 3, Binding = 0, Format = VkFormat.R32G32B32A32Sfloat, Offset = 24 };
			var vertexInput = arena.Write(new PipelineVertexInputStateCreateInfo
			{
				SType = StructureType.PipelineVertexInputStateCreateInfo,
				VertexBindingDescriptionCount = 1,
				PVertexBindingDescriptions = binding,
				VertexAttributeDescriptionCount = 4,
				PVertexAttributeDescriptions = attributes,
			});

			var inputAssembly = arena.Write(new PipelineInputAssemblyStateCreateInfo
			{
				SType = StructureType.PipelineInputAssemblyStateCreateInfo,
				Topology = PrimitiveTopology.TriangleList,
			});
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
			// Premultiplied alpha-over: the compositor folds opacity into the (premultiplied) vertex colour, so
			// src factor is ONE (not SRC_ALPHA).
			var blendAttachment = arena.Write(new PipelineColorBlendAttachmentState
			{
				BlendEnable = true,
				SrcColorBlendFactor = BlendFactor.One,
				DstColorBlendFactor = BlendFactor.OneMinusSrcAlpha,
				ColorBlendOp = BlendOp.Add,
				SrcAlphaBlendFactor = BlendFactor.One,
				DstAlphaBlendFactor = BlendFactor.OneMinusSrcAlpha,
				AlphaBlendOp = BlendOp.Add,
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

			var format = colorFormat;
			var renderingInfo = arena.Write(new PipelineRenderingCreateInfo
			{
				SType = StructureType.PipelineRenderingCreateInfo,
				ColorAttachmentCount = 1,
				PColorAttachmentFormats = &format,
				DepthAttachmentFormat = VkFormat.Undefined,
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
				PDepthStencilState = null,
				PDynamicState = dynamicState,
				Layout = _layout,
				RenderPass = default, // dynamic rendering
			};
			_vk.CreateGraphicsPipelines(_ctx.Device, default, 1, in createInfo, null, out _pipeline).Check("vkCreateGraphicsPipelines");
			_ctx.SetObjectName(ObjectType.Pipeline, _pipeline.Handle, "pipeline:ui");
		}
		finally
		{
			_vk.DestroyShaderModule(_ctx.Device, module, null);
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
			_vk.CreateShaderModule(_ctx.Device, in info, null, out var module).Check("vkCreateShaderModule");
			return module;
		}
	}

	// Upload tightly packed RGBA8 pixels into texture-table slot <paramref name="index"/> (device-local image via a
	// host-visible staging copy, the MeshStore/ImGui pattern) and point that slot's descriptor at it. Linear/UNORM
	// format — the atlas carries SDF distances, never sRGB colour.
	private void UploadTexture(int index, int width, int height, ReadOnlySpan<byte> rgba, CommandPool commandPool)
	{
		var uploadSize = (ulong)((long)width * height * 4);

		ImageCreateInfo imageInfo = new()
		{
			SType = StructureType.ImageCreateInfo,
			ImageType = ImageType.Type2D,
			Format = VkFormat.R8G8B8A8Unorm,
			Extent = new Extent3D((uint)width, (uint)height, 1),
			MipLevels = 1,
			ArrayLayers = 1,
			Samples = SampleCountFlags.Count1Bit,
			Tiling = ImageTiling.Optimal,
			Usage = ImageUsageFlags.SampledBit | ImageUsageFlags.TransferDstBit,
			SharingMode = SharingMode.Exclusive,
			InitialLayout = ImageLayout.Undefined,
		};
		_vk.CreateImage(_ctx.Device, in imageInfo, null, out _texImages[index]).Check("vkCreateImage");
		_vk.GetImageMemoryRequirements(_ctx.Device, _texImages[index], out var req);
		MemoryRequirements reqs = new() { Size = req.Size, Alignment = req.Alignment, MemoryTypeBits = req.MemoryTypeBits };
		_texAllocs[index] = _allocator.AllocateMemory(in reqs, new AllocationCreateInfo { Usage = MemoryUsage.GPU_Only });
		_texAllocs[index]!.BindImageMemory(_texImages[index]).Check("vmaBindImageMemory");

		ImageViewCreateInfo viewInfo = new()
		{
			SType = StructureType.ImageViewCreateInfo,
			Image = _texImages[index],
			ViewType = ImageViewType.Type2D,
			Format = VkFormat.R8G8B8A8Unorm,
			SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1),
		};
		_vk.CreateImageView(_ctx.Device, in viewInfo, null, out _texViews[index]).Check("vkCreateImageView");

		BufferCreateInfo stagingInfo = new()
		{
			SType = StructureType.BufferCreateInfo,
			Size = uploadSize,
			Usage = BufferUsageFlags.TransferSrcBit,
			SharingMode = SharingMode.Exclusive,
		};
		var staging = _allocator.CreateBuffer(in stagingInfo, new AllocationCreateInfo { Usage = MemoryUsage.CPU_Only }, out var stagingAllocation);
		var mapped = stagingAllocation.Map();
		fixed (byte* src = rgba)
			System.Buffer.MemoryCopy(src, (void*)mapped, (long)uploadSize, (long)uploadSize);
		stagingAllocation.Unmap();

		var cmd = BeginOneTime(commandPool);
		var range = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1);
		TransitionImage(cmd, _texImages[index], range,
			ImageLayout.Undefined, ImageLayout.TransferDstOptimal,
			PipelineStageFlags2.TopOfPipeBit, AccessFlags2.None,
			PipelineStageFlags2.AllTransferBit, AccessFlags2.TransferWriteBit);
		BufferImageCopy region = new()
		{
			ImageSubresource = new ImageSubresourceLayers { AspectMask = ImageAspectFlags.ColorBit, LayerCount = 1 },
			ImageExtent = new Extent3D((uint)width, (uint)height, 1),
		};
		_vk.CmdCopyBufferToImage(cmd, staging, _texImages[index], ImageLayout.TransferDstOptimal, 1, in region);
		TransitionImage(cmd, _texImages[index], range,
			ImageLayout.TransferDstOptimal, ImageLayout.ShaderReadOnlyOptimal,
			PipelineStageFlags2.AllTransferBit, AccessFlags2.TransferWriteBit,
			PipelineStageFlags2.FragmentShaderBit, AccessFlags2.ShaderSampledReadBit);
		EndOneTime(commandPool, cmd);

		_vk.DestroyBuffer(_ctx.Device, staging, null);
		stagingAllocation.Dispose();

		DescriptorImageInfo descImage = new()
		{
			Sampler = _sampler,
			ImageView = _texViews[index],
			ImageLayout = ImageLayout.ShaderReadOnlyOptimal,
		};
		WriteDescriptorSet write = new()
		{
			SType = StructureType.WriteDescriptorSet,
			DstSet = _texSets[index],
			DstBinding = 0,
			DescriptorCount = 1,
			DescriptorType = DescriptorType.CombinedImageSampler,
			PImageInfo = &descImage,
		};
		_vk.UpdateDescriptorSets(_ctx.Device, 1, &write, 0, null);
	}

	/// <summary>
	/// Record one surface's composited <see cref="UIDrawData"/> into the (already-open) "ui" pass scope. Empty
	/// draw data is a no-op (a disabled or fully-clean surface).
	/// </summary>
	public void Render(CommandBuffer cmd, UIDrawData draw, Extent2D fbExtent, int frameSlot)
	{
		var vertices = draw.Vertices.Span;
		var indices = draw.Indices.Span;
		var commands = draw.Commands.Span;
		if (vertices.Length == 0 || indices.Length == 0 || commands.Length == 0)
			return;
		if (draw.SurfaceSize.X <= 0 || draw.SurfaceSize.Y <= 0)
			return;

		var vtxBytes = (ulong)(vertices.Length * sizeof(UIVertex));
		var idxBytes = (ulong)(indices.Length * sizeof(ushort));
		EnsureBuffer(ref _vtxBuffers[frameSlot], ref _vtxAllocations[frameSlot], ref _vtxSizes[frameSlot], vtxBytes, BufferUsageFlags.VertexBufferBit);
		EnsureBuffer(ref _idxBuffers[frameSlot], ref _idxAllocations[frameSlot], ref _idxSizes[frameSlot], idxBytes, BufferUsageFlags.IndexBufferBit);

		var vtxDst = (byte*)_vtxAllocations[frameSlot]!.Map();
		MemoryMarshal.AsBytes(vertices).CopyTo(new Span<byte>(vtxDst, (int)vtxBytes));
		_vtxAllocations[frameSlot]!.Unmap();
		var idxDst = (byte*)_idxAllocations[frameSlot]!.Map();
		MemoryMarshal.AsBytes(indices).CopyTo(new Span<byte>(idxDst, (int)idxBytes));
		_idxAllocations[frameSlot]!.Unmap();

		_vk.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, _pipeline);
		var vtxBuffer = _vtxBuffers[frameSlot];
		ulong vbOffset = 0;
		_vk.CmdBindVertexBuffers(cmd, 0, 1, in vtxBuffer, in vbOffset);
		_vk.CmdBindIndexBuffer(cmd, _idxBuffers[frameSlot], 0, IndexType.Uint16);

		Viewport viewport = new() { X = 0, Y = 0, Width = fbExtent.Width, Height = fbExtent.Height, MinDepth = 0, MaxDepth = 1 };
		_vk.CmdSetViewport(cmd, 0, 1, in viewport);

		// Surface-pixel → clip (no Y-flip; the compositor authors +Y-down, matching Vulkan clip). Premultiplied
		// HDR colour is lifted to paper white in the shader when the surface is HDR.
		var scaleX = 2.0f / draw.SurfaceSize.X;
		var scaleY = 2.0f / draw.SurfaceSize.Y;
		var paperWhite = draw.IsHdrSurface ? HdrPaperWhiteScale : 1.0f;
		var isHdr = draw.IsHdrSurface ? 1u : 0u;

		var push = stackalloc float[7];
		push[0] = scaleX;
		push[1] = scaleY;
		push[2] = -1.0f;
		push[3] = -1.0f;
		push[5] = paperWhite;
		*(uint*)(push + 6) = isHdr;

		foreach (var c in commands)
		{
			var clip = c.ClipRect;
			var minX = MathF.Max(0, clip.X);
			var minY = MathF.Max(0, clip.Y);
			var maxX = MathF.Min(fbExtent.Width, clip.X + clip.Width);
			var maxY = MathF.Min(fbExtent.Height, clip.Y + clip.Height);
			if (maxX <= minX || maxY <= minY)
				continue;

			*(uint*)(push + 4) = (uint)c.Kind;
			_vk.CmdPushConstants(cmd, _layout, ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit, 0, PushSize, push);

			var set = _texSets[Math.Clamp(c.TextureId, 0, _texCount - 1)];
			_vk.CmdBindDescriptorSets(cmd, PipelineBindPoint.Graphics, _layout, 0, 1, in set, 0, null);

			Rect2D scissor = new(new Offset2D((int)minX, (int)minY), new Extent2D((uint)(maxX - minX), (uint)(maxY - minY)));
			_vk.CmdSetScissor(cmd, 0, 1, in scissor);

			_vk.CmdDrawIndexed(cmd, (uint)c.IndexCount, 1, (uint)c.IndexOffset, c.VertexOffset, 0);
		}
	}

	// Grow (or first-create) a host-visible buffer to hold at least <paramref name="needed"/> bytes. The slot's
	// fence has been waited before this slot records again, so destroying/replacing the buffer can't race the GPU.
	private void EnsureBuffer(ref Buffer buffer, ref Allocation? allocation, ref ulong currentSize, ulong needed, BufferUsageFlags usage)
	{
		if (needed == 0)
			needed = 256;
		if (buffer.Handle != 0 && currentSize >= needed)
			return;
		if (buffer.Handle != 0)
		{
			_vk.DestroyBuffer(_ctx.Device, buffer, null);
			allocation!.Dispose();
		}
		var grown = needed + needed / 2; // headroom so a frame's growth doesn't reallocate next frame
		BufferCreateInfo info = new()
		{
			SType = StructureType.BufferCreateInfo,
			Size = grown,
			Usage = usage,
			SharingMode = SharingMode.Exclusive,
		};
		buffer = _allocator.CreateBuffer(in info, new AllocationCreateInfo { Usage = MemoryUsage.CPU_Only }, out var alloc);
		allocation = alloc;
		currentSize = grown;
	}

	private CommandBuffer BeginOneTime(CommandPool pool)
	{
		CommandBufferAllocateInfo allocInfo = new()
		{
			SType = StructureType.CommandBufferAllocateInfo,
			CommandPool = pool,
			Level = CommandBufferLevel.Primary,
			CommandBufferCount = 1,
		};
		_vk.AllocateCommandBuffers(_ctx.Device, in allocInfo, out var cmd).Check("vkAllocateCommandBuffers");
		CommandBufferBeginInfo begin = new()
		{
			SType = StructureType.CommandBufferBeginInfo,
			Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
		};
		_vk.BeginCommandBuffer(cmd, in begin).Check("vkBeginCommandBuffer");
		return cmd;
	}

	private void EndOneTime(CommandPool pool, CommandBuffer cmd)
	{
		_vk.EndCommandBuffer(cmd).Check("vkEndCommandBuffer");
		SubmitInfo submit = new()
		{
			SType = StructureType.SubmitInfo,
			CommandBufferCount = 1,
			PCommandBuffers = &cmd,
		};
		_vk.QueueSubmit(_ctx.GraphicsQueue, 1, in submit, default).Check("vkQueueSubmit");
		_vk.QueueWaitIdle(_ctx.GraphicsQueue);
		_vk.FreeCommandBuffers(_ctx.Device, pool, 1, in cmd);
	}

	private void TransitionImage(
		CommandBuffer cmd, Image image, ImageSubresourceRange range,
		ImageLayout oldLayout, ImageLayout newLayout,
		PipelineStageFlags2 srcStage, AccessFlags2 srcAccess,
		PipelineStageFlags2 dstStage, AccessFlags2 dstAccess)
	{
		ImageMemoryBarrier2 barrier = new()
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
			SubresourceRange = range,
		};
		DependencyInfo dependency = new()
		{
			SType = StructureType.DependencyInfo,
			ImageMemoryBarrierCount = 1,
			PImageMemoryBarriers = &barrier,
		};
		_vk.CmdPipelineBarrier2(cmd, in dependency);
	}

	public void Dispose()
	{
		if (_pipeline.Handle != 0)
			_vk.DestroyPipeline(_ctx.Device, _pipeline, null);
		if (_layout.Handle != 0)
			_vk.DestroyPipelineLayout(_ctx.Device, _layout, null);
		if (_descriptorPool.Handle != 0)
			_vk.DestroyDescriptorPool(_ctx.Device, _descriptorPool, null);
		if (_setLayout.Handle != 0)
			_vk.DestroyDescriptorSetLayout(_ctx.Device, _setLayout, null);
		if (_sampler.Handle != 0)
			_vk.DestroySampler(_ctx.Device, _sampler, null);

		for (var i = 0; i < _texCount; i++)
		{
			if (_texViews[i].Handle != 0)
				_vk.DestroyImageView(_ctx.Device, _texViews[i], null);
			if (_texImages[i].Handle != 0)
				_vk.DestroyImage(_ctx.Device, _texImages[i], null);
			_texAllocs[i]?.Dispose();
		}

		for (var i = 0; i < _framesInFlight; i++)
		{
			if (_vtxBuffers[i].Handle != 0)
			{
				_vk.DestroyBuffer(_ctx.Device, _vtxBuffers[i], null);
				_vtxAllocations[i]?.Dispose();
			}
			if (_idxBuffers[i].Handle != 0)
			{
				_vk.DestroyBuffer(_ctx.Device, _idxBuffers[i], null);
				_idxAllocations[i]?.Dispose();
			}
		}
	}
}
