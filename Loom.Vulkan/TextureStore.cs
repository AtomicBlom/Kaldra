using Loom.Core;
using Loom.Core.Materials;
using Silk.NET.Vulkan;
using VMASharp;

using Buffer = Silk.NET.Vulkan.Buffer;
using VkFormat = Silk.NET.Vulkan.Format;

namespace Loom.Vulkan;

/// <summary>
/// Owns GPU-resident textures: device-local sampled images with a full mip chain, uploaded once via a
/// host-visible staging copy (the texture counterpart of <see cref="MeshStore"/>, PlanningPhase2.md M11). The
/// app builds textures up front through the engine and gets back an opaque <see cref="Texture"/> handle; a
/// material draw resolves the handle to its image view and binds it as a combined image sampler. Mip levels
/// arrive pre-built on the CPU in <see cref="TextureData"/>, so the store just uploads each level — no GPU
/// blit/mip-gen, and byte-identical mips across backends. Images are long-lived; freed only on teardown.
/// </summary>
internal sealed unsafe class TextureStore : IDisposable
{
	private struct StoredTexture
	{
		public Image Image;
		public Allocation Allocation;
		public ImageView View;
	}

	private readonly VulkanContext _ctx;
	private readonly Vk _vk;
	private readonly VulkanMemoryAllocator _allocator;
	private readonly CommandPool _commandPool;
	private readonly List<StoredTexture> _textures = [];

	public TextureStore(VulkanContext ctx, VulkanMemoryAllocator allocator, CommandPool commandPool)
	{
		_ctx = ctx;
		_vk = ctx.Vk;
		_allocator = allocator;
		_commandPool = commandPool;
	}

	/// <summary>Upload a texture + its mip chain and return a handle a material can bundle.</summary>
	public Texture Create(TextureData data)
	{
		var format = data.Format == TextureFormat.Rgba8Srgb ? VkFormat.R8G8B8A8Srgb : VkFormat.R8G8B8A8Unorm;
		var mipLevels = (uint)data.Levels.Count;

		// Device-local sampled image with the full mip chain.
		ImageCreateInfo imageInfo = new()
		{
			SType = StructureType.ImageCreateInfo,
			ImageType = ImageType.Type2D,
			Format = format,
			Extent = new Extent3D((uint)data.Width, (uint)data.Height, 1),
			MipLevels = mipLevels,
			ArrayLayers = 1,
			Samples = SampleCountFlags.Count1Bit,
			Tiling = ImageTiling.Optimal,
			Usage = ImageUsageFlags.SampledBit | ImageUsageFlags.TransferDstBit,
			SharingMode = SharingMode.Exclusive,
			InitialLayout = ImageLayout.Undefined,
		};
		_vk.CreateImage(_ctx.Device, in imageInfo, null, out var image).Check("vkCreateImage");
		_vk.GetImageMemoryRequirements(_ctx.Device, image, out var req);
		MemoryRequirements reqs = new() { Size = req.Size, Alignment = req.Alignment, MemoryTypeBits = req.MemoryTypeBits };
		var allocation = _allocator.AllocateMemory(in reqs, new AllocationCreateInfo { Usage = MemoryUsage.GPU_Only });
		allocation.BindImageMemory(image).Check("vmaBindImageMemory");

		ImageViewCreateInfo viewInfo = new()
		{
			SType = StructureType.ImageViewCreateInfo,
			Image = image,
			ViewType = ImageViewType.Type2D,
			Format = format,
			SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, mipLevels, 0, 1),
		};
		_vk.CreateImageView(_ctx.Device, in viewInfo, null, out var view).Check("vkCreateImageView");

		UploadLevels(image, data, mipLevels);

		var id = _textures.Count;
		_textures.Add(new StoredTexture { Image = image, Allocation = allocation, View = view });
		return new Texture(id, data.Width, data.Height);
	}

	/// <summary>The sampled image view a material binding points at.</summary>
	public ImageView View(int textureId) => _textures[textureId].View;

	// Stage every mip level (tightly packed, concatenated) in one host-visible buffer, transition the whole mip
	// range Undefined→TransferDst, copy each level, then transition the whole range to ShaderReadOnly.
	private void UploadLevels(Image image, TextureData data, uint mipLevels)
	{
		ulong total = 0;
		foreach (var level in data.Levels)
			total += (ulong)level.Pixels.Length;

		BufferCreateInfo stagingInfo = new()
		{
			SType = StructureType.BufferCreateInfo,
			Size = total,
			Usage = BufferUsageFlags.TransferSrcBit,
			SharingMode = SharingMode.Exclusive,
		};
		var staging = _allocator.CreateBuffer(in stagingInfo, new AllocationCreateInfo { Usage = MemoryUsage.CPU_Only }, out var stagingAllocation);

		var mapped = (byte*)stagingAllocation.Map();
		var copies = new BufferImageCopy[mipLevels];
		ulong offset = 0;
		for (var i = 0; i < data.Levels.Count; i++)
		{
			var level = data.Levels[i];
			level.Pixels.AsSpan().CopyTo(new Span<byte>(mapped + offset, level.Pixels.Length));
			copies[i] = new BufferImageCopy
			{
				BufferOffset = offset,
				ImageSubresource = new ImageSubresourceLayers { AspectMask = ImageAspectFlags.ColorBit, MipLevel = (uint)i, LayerCount = 1 },
				ImageExtent = new Extent3D((uint)level.Width, (uint)level.Height, 1),
			};
			offset += (ulong)level.Pixels.Length;
		}
		stagingAllocation.Unmap();

		var range = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, mipLevels, 0, 1);
		var cmd = BeginOneTime();
		TransitionImage(cmd, image, range,
			ImageLayout.Undefined, ImageLayout.TransferDstOptimal,
			PipelineStageFlags2.TopOfPipeBit, AccessFlags2.None,
			PipelineStageFlags2.AllTransferBit, AccessFlags2.TransferWriteBit);
		fixed (BufferImageCopy* pCopies = copies)
			_vk.CmdCopyBufferToImage(cmd, staging, image, ImageLayout.TransferDstOptimal, mipLevels, pCopies);
		TransitionImage(cmd, image, range,
			ImageLayout.TransferDstOptimal, ImageLayout.ShaderReadOnlyOptimal,
			PipelineStageFlags2.AllTransferBit, AccessFlags2.TransferWriteBit,
			PipelineStageFlags2.FragmentShaderBit, AccessFlags2.ShaderSampledReadBit);
		EndOneTime(cmd);

		_vk.DestroyBuffer(_ctx.Device, staging, null);
		stagingAllocation.Dispose();
	}

	private CommandBuffer BeginOneTime()
	{
		CommandBufferAllocateInfo allocInfo = new()
		{
			SType = StructureType.CommandBufferAllocateInfo,
			CommandPool = _commandPool,
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

	private void EndOneTime(CommandBuffer cmd)
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
		_vk.FreeCommandBuffers(_ctx.Device, _commandPool, 1, in cmd);
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
		foreach (var texture in _textures)
		{
			if (texture.View.Handle != 0)
				_vk.DestroyImageView(_ctx.Device, texture.View, null);
			if (texture.Image.Handle != 0)
				_vk.DestroyImage(_ctx.Device, texture.Image, null);
			texture.Allocation.Dispose();
		}
		_textures.Clear();
	}
}
