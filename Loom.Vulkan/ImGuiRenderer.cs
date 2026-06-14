// The Vulkan Dear ImGui renderer is compiled only when the engine is built with ImGui (UseImGui=true).
// When off, the whole type is excluded and ImGui.NET is not referenced (Loom.Vulkan.csproj); the neutral
// ctx.DrawImGui() / EnableImGui() seam in Loom.Core stays, as documented no-ops.
#if LOOM_IMGUI
using System.Numerics;

using ImGuiNET;
using Silk.NET.Vulkan;
using VMASharp;

using Buffer = Silk.NET.Vulkan.Buffer;
using VkFormat = Silk.NET.Vulkan.Format;

namespace Loom.Vulkan;

/// <summary>
/// Loom's Dear ImGui renderer — the <c>imgui_impl_vulkan</c> equivalent. ImGui rendering is inherently
/// backend-specific (it bakes a pipeline, uploads a font atlas, and translates <c>ImDrawData</c> into draw
/// calls), so it is quarantined here in the backend; Loom.Core/Graph stay UI-agnostic, reaching it only
/// through the neutral <c>ctx.DrawImGui()</c> verb. The host owns the ImGui context + input and hands the
/// executor the draw-data pointer each frame; this class just consumes it.
/// <para>
/// It records <em>into</em> an already-open dynamic-rendering scope — the graph's "hud" pass, which
/// <c>.Blends</c> the backbuffer at native resolution after the §9 pixelation upscale, so the UI is crisp and
/// untouched by bloom/pixelation. It never begins/ends rendering itself. The font atlas + its descriptor set
/// are created once; vertex/index buffers are host-visible and grown per frame-in-flight slot (the slot's
/// fence has been waited before the executor records it again, so reusing the buffer is hazard-free).
/// </para>
/// </summary>
internal sealed unsafe class ImGuiRenderer : IDisposable
{
	private readonly VulkanContext _ctx;
	private readonly Vk _vk;
	private readonly VulkanMemoryAllocator _allocator;

	private Pipeline _pipeline;
	private PipelineLayout _layout;
	private DescriptorSetLayout _setLayout;
	private DescriptorPool _descriptorPool;
	private DescriptorSet _fontSet;
	private Sampler _fontSampler;

	private Image _fontImage;
	private ImageView _fontView;
	private Allocation _fontAllocation = null!;

	// Per frame-in-flight slot: a host-visible vertex + index buffer, grown on demand (ImGui geometry size
	// varies with what the UI draws). Multi-buffered like the transients so a frame's upload can't clobber a
	// still-in-flight earlier frame's draw of its own slot.
	private readonly int _framesInFlight;
	private readonly Buffer[] _vtxBuffers;
	private readonly Allocation?[] _vtxAllocations;
	private readonly ulong[] _vtxSizes;
	private readonly Buffer[] _idxBuffers;
	private readonly Allocation?[] _idxAllocations;
	private readonly ulong[] _idxSizes;

	private const uint PushSize = 4 * sizeof(float); // scale (float2) + translate (float2)

	public ImGuiRenderer(VulkanContext ctx, VulkanMemoryAllocator allocator, CommandPool commandPool, VkFormat colorFormat, byte[] spirv, int framesInFlight)
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

		// We index the concatenated vertex buffer per draw-cmd, so advertise VtxOffset support.
		ImGui.GetIO().BackendFlags |= ImGuiBackendFlags.RendererHasVtxOffset;

		CreateFontSampler();
		CreateDescriptors();
		CreatePipeline(colorFormat, spirv);
		UploadFontAtlas(commandPool);
	}

	private void CreateFontSampler()
	{
		SamplerCreateInfo info = new()
		{
			SType = StructureType.SamplerCreateInfo,
			MagFilter = Filter.Linear,
			MinFilter = Filter.Linear,
			MipmapMode = SamplerMipmapMode.Linear,
			AddressModeU = SamplerAddressMode.Repeat,
			AddressModeV = SamplerAddressMode.Repeat,
			AddressModeW = SamplerAddressMode.Repeat,
			MinLod = -1000,
			MaxLod = 1000,
		};
		_vk.CreateSampler(_ctx.Device, in info, null, out _fontSampler).Check("vkCreateSampler");
	}

	// One combined image sampler (the font atlas) in one persistent set — bound for every draw regardless of
	// the cmd's TextureId (the control panel only ever uses the font atlas).
	private void CreateDescriptors()
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

		DescriptorPoolSize poolSize = new() { Type = DescriptorType.CombinedImageSampler, DescriptorCount = 1 };
		DescriptorPoolCreateInfo poolInfo = new()
		{
			SType = StructureType.DescriptorPoolCreateInfo,
			MaxSets = 1,
			PoolSizeCount = 1,
			PPoolSizes = &poolSize,
		};
		_vk.CreateDescriptorPool(_ctx.Device, in poolInfo, null, out _descriptorPool).Check("vkCreateDescriptorPool");

		var setLayout = _setLayout;
		DescriptorSetAllocateInfo allocInfo = new()
		{
			SType = StructureType.DescriptorSetAllocateInfo,
			DescriptorPool = _descriptorPool,
			DescriptorSetCount = 1,
			PSetLayouts = &setLayout,
		};
		_vk.AllocateDescriptorSets(_ctx.Device, in allocInfo, out _fontSet).Check("vkAllocateDescriptorSets");
	}

	// The ImGui pipeline: ImDrawVert layout, alpha-over blend, no depth, dynamic viewport/scissor, baked
	// against the swapchain colour format via dynamic rendering. Mirrors Technique's bake, bespoke for ImGui.
	private void CreatePipeline(VkFormat colorFormat, byte[] spirv)
	{
		var module = CreateShaderModule(spirv);
		try
		{
			var setLayout = _setLayout;
			PushConstantRange pushRange = new() { StageFlags = ShaderStageFlags.VertexBit, Offset = 0, Size = PushSize };
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

			// ImDrawVert: pos (float2 @0), uv (float2 @8), col (R8G8B8A8_UNORM @16) — stride 20.
			var binding = arena.Write(new VertexInputBindingDescription
			{
				Binding = 0,
				Stride = (uint)sizeof(ImDrawVert),
				InputRate = VertexInputRate.Vertex,
			});
			var attributes = arena.Alloc<VertexInputAttributeDescription>(3);
			attributes[0] = new VertexInputAttributeDescription { Location = 0, Binding = 0, Format = VkFormat.R32G32Sfloat, Offset = 0 };
			attributes[1] = new VertexInputAttributeDescription { Location = 1, Binding = 0, Format = VkFormat.R32G32Sfloat, Offset = 8 };
			attributes[2] = new VertexInputAttributeDescription { Location = 2, Binding = 0, Format = VkFormat.R8G8B8A8Unorm, Offset = 16 };
			var vertexInput = arena.Write(new PipelineVertexInputStateCreateInfo
			{
				SType = StructureType.PipelineVertexInputStateCreateInfo,
				VertexBindingDescriptionCount = 1,
				PVertexBindingDescriptions = binding,
				VertexAttributeDescriptionCount = 3,
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
			// Canonical ImGui blend: src-alpha over for colour, and accumulate coverage into alpha.
			var blendAttachment = arena.Write(new PipelineColorBlendAttachmentState
			{
				BlendEnable = true,
				SrcColorBlendFactor = BlendFactor.SrcAlpha,
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

	// Build ImGui's font atlas, upload it to a device-local image via a host-visible staging copy (the
	// MeshStore pattern), and point the persistent font descriptor at it.
	private void UploadFontAtlas(CommandPool commandPool)
	{
		var io = ImGui.GetIO();
		io.Fonts.GetTexDataAsRGBA32(out IntPtr pixels, out int width, out int height, out int _);
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
		_vk.CreateImage(_ctx.Device, in imageInfo, null, out _fontImage).Check("vkCreateImage");
		_vk.GetImageMemoryRequirements(_ctx.Device, _fontImage, out var req);
		MemoryRequirements reqs = new() { Size = req.Size, Alignment = req.Alignment, MemoryTypeBits = req.MemoryTypeBits };
		_fontAllocation = _allocator.AllocateMemory(in reqs, new AllocationCreateInfo { Usage = MemoryUsage.GPU_Only });
		_fontAllocation.BindImageMemory(_fontImage).Check("vmaBindImageMemory");

		ImageViewCreateInfo viewInfo = new()
		{
			SType = StructureType.ImageViewCreateInfo,
			Image = _fontImage,
			ViewType = ImageViewType.Type2D,
			Format = VkFormat.R8G8B8A8Unorm,
			SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1),
		};
		_vk.CreateImageView(_ctx.Device, in viewInfo, null, out _fontView).Check("vkCreateImageView");

		BufferCreateInfo stagingInfo = new()
		{
			SType = StructureType.BufferCreateInfo,
			Size = uploadSize,
			Usage = BufferUsageFlags.TransferSrcBit,
			SharingMode = SharingMode.Exclusive,
		};
		var staging = _allocator.CreateBuffer(in stagingInfo, new AllocationCreateInfo { Usage = MemoryUsage.CPU_Only }, out var stagingAllocation);
		var mapped = stagingAllocation.Map();
		System.Buffer.MemoryCopy((void*)pixels, (void*)mapped, (long)uploadSize, (long)uploadSize);
		stagingAllocation.Unmap();

		var cmd = BeginOneTime(commandPool);
		var range = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1);
		TransitionImage(cmd, _fontImage, range,
			ImageLayout.Undefined, ImageLayout.TransferDstOptimal,
			PipelineStageFlags2.TopOfPipeBit, AccessFlags2.None,
			PipelineStageFlags2.AllTransferBit, AccessFlags2.TransferWriteBit);
		BufferImageCopy region = new()
		{
			ImageSubresource = new ImageSubresourceLayers { AspectMask = ImageAspectFlags.ColorBit, LayerCount = 1 },
			ImageExtent = new Extent3D((uint)width, (uint)height, 1),
		};
		_vk.CmdCopyBufferToImage(cmd, staging, _fontImage, ImageLayout.TransferDstOptimal, 1, in region);
		TransitionImage(cmd, _fontImage, range,
			ImageLayout.TransferDstOptimal, ImageLayout.ShaderReadOnlyOptimal,
			PipelineStageFlags2.AllTransferBit, AccessFlags2.TransferWriteBit,
			PipelineStageFlags2.FragmentShaderBit, AccessFlags2.ShaderSampledReadBit);
		EndOneTime(commandPool, cmd);

		_vk.DestroyBuffer(_ctx.Device, staging, null);
		stagingAllocation.Dispose();

		DescriptorImageInfo descImage = new()
		{
			Sampler = _fontSampler,
			ImageView = _fontView,
			ImageLayout = ImageLayout.ShaderReadOnlyOptimal,
		};
		WriteDescriptorSet write = new()
		{
			SType = StructureType.WriteDescriptorSet,
			DstSet = _fontSet,
			DstBinding = 0,
			DescriptorCount = 1,
			DescriptorType = DescriptorType.CombinedImageSampler,
			PImageInfo = &descImage,
		};
		_vk.UpdateDescriptorSets(_ctx.Device, 1, &write, 0, null);

		io.Fonts.SetTexID((IntPtr)1); // non-null id; we always bind the one font set
	}

	/// <summary>
	/// Record the ImGui draw data into the (already-open) hud-pass rendering scope. <paramref name="drawDataPtr"/>
	/// is the native <c>ImDrawData*</c> the host produced via <c>ImGui.Render()</c>; null/empty means no UI.
	/// </summary>
	public void Render(CommandBuffer cmd, IntPtr drawDataPtr, Extent2D fbExtent, int frameSlot)
	{
		if (drawDataPtr == IntPtr.Zero)
			return;
		var drawData = new ImDrawDataPtr((ImDrawData*)drawDataPtr);
		if (drawData.CmdListsCount == 0 || drawData.TotalVtxCount == 0 || drawData.DisplaySize.X <= 0 || drawData.DisplaySize.Y <= 0)
			return;

		var vtxStride = sizeof(ImDrawVert);
		var vtxBytes = (ulong)((long)drawData.TotalVtxCount * vtxStride);
		var idxBytes = (ulong)((long)drawData.TotalIdxCount * sizeof(ushort));
		EnsureBuffer(ref _vtxBuffers[frameSlot], ref _vtxAllocations[frameSlot], ref _vtxSizes[frameSlot], vtxBytes, BufferUsageFlags.VertexBufferBit);
		EnsureBuffer(ref _idxBuffers[frameSlot], ref _idxAllocations[frameSlot], ref _idxSizes[frameSlot], idxBytes, BufferUsageFlags.IndexBufferBit);

		// Concatenate every cmd-list's vertices/indices into the slot's host-visible buffers.
		var vtxDst = (byte*)_vtxAllocations[frameSlot]!.Map();
		var idxDst = (byte*)_idxAllocations[frameSlot]!.Map();
		long vtxOffset = 0;
		long idxOffset = 0;
		for (var n = 0; n < drawData.CmdListsCount; n++)
		{
			var list = drawData.CmdLists[n];
			var vtxSrcBytes = (long)list.VtxBuffer.Size * vtxStride;
			var idxSrcBytes = (long)list.IdxBuffer.Size * sizeof(ushort);
			System.Buffer.MemoryCopy((void*)list.VtxBuffer.Data, vtxDst + vtxOffset, (long)vtxBytes - vtxOffset, vtxSrcBytes);
			System.Buffer.MemoryCopy((void*)list.IdxBuffer.Data, idxDst + idxOffset, (long)idxBytes - idxOffset, idxSrcBytes);
			vtxOffset += vtxSrcBytes;
			idxOffset += idxSrcBytes;
		}
		_vtxAllocations[frameSlot]!.Unmap();
		_idxAllocations[frameSlot]!.Unmap();

		_vk.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, _pipeline);
		var set = _fontSet;
		_vk.CmdBindDescriptorSets(cmd, PipelineBindPoint.Graphics, _layout, 0, 1, in set, 0, null);
		var vtxBuffer = _vtxBuffers[frameSlot];
		ulong vbOffset = 0;
		_vk.CmdBindVertexBuffers(cmd, 0, 1, in vtxBuffer, in vbOffset);
		_vk.CmdBindIndexBuffer(cmd, _idxBuffers[frameSlot], 0, IndexType.Uint16);

		Viewport viewport = new() { X = 0, Y = 0, Width = fbExtent.Width, Height = fbExtent.Height, MinDepth = 0, MaxDepth = 1 };
		_vk.CmdSetViewport(cmd, 0, 1, in viewport);

		// Map ImGui pixel coordinates to clip space (no Y-flip: ImGui's +Y-down origin matches Vulkan clip).
		var displayPos = drawData.DisplayPos;
		var displaySize = drawData.DisplaySize;
		var push = stackalloc float[4];
		push[0] = 2.0f / displaySize.X;
		push[1] = 2.0f / displaySize.Y;
		push[2] = -1.0f - displayPos.X * push[0];
		push[3] = -1.0f - displayPos.Y * push[1];
		_vk.CmdPushConstants(cmd, _layout, ShaderStageFlags.VertexBit, 0, PushSize, push);

		var clipOff = displayPos;
		var clipScale = drawData.FramebufferScale;
		if (clipScale.X <= 0 || clipScale.Y <= 0)
			clipScale = Vector2.One; // host didn't set DisplayFramebufferScale — assume 1:1
		var globalVtx = 0;
		var globalIdx = 0;
		for (var n = 0; n < drawData.CmdListsCount; n++)
		{
			var list = drawData.CmdLists[n];
			for (var c = 0; c < list.CmdBuffer.Size; c++)
			{
				var pcmd = list.CmdBuffer[c];
				var minX = (pcmd.ClipRect.X - clipOff.X) * clipScale.X;
				var minY = (pcmd.ClipRect.Y - clipOff.Y) * clipScale.Y;
				var maxX = (pcmd.ClipRect.Z - clipOff.X) * clipScale.X;
				var maxY = (pcmd.ClipRect.W - clipOff.Y) * clipScale.Y;
				if (minX < 0) minX = 0;
				if (minY < 0) minY = 0;
				if (maxX > fbExtent.Width) maxX = fbExtent.Width;
				if (maxY > fbExtent.Height) maxY = fbExtent.Height;
				if (maxX <= minX || maxY <= minY)
					continue;

				Rect2D scissor = new(new Offset2D((int)minX, (int)minY), new Extent2D((uint)(maxX - minX), (uint)(maxY - minY)));
				_vk.CmdSetScissor(cmd, 0, 1, in scissor);
				_vk.CmdDrawIndexed(cmd, pcmd.ElemCount, 1, pcmd.IdxOffset + (uint)globalIdx, (int)(pcmd.VtxOffset + (uint)globalVtx), 0);
			}
			globalIdx += list.IdxBuffer.Size;
			globalVtx += list.VtxBuffer.Size;
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
		if (_fontSampler.Handle != 0)
			_vk.DestroySampler(_ctx.Device, _fontSampler, null);
		if (_fontView.Handle != 0)
			_vk.DestroyImageView(_ctx.Device, _fontView, null);
		if (_fontImage.Handle != 0)
			_vk.DestroyImage(_ctx.Device, _fontImage, null);
		_fontAllocation?.Dispose();

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
#endif
