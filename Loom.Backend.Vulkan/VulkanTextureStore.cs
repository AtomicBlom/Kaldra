using Loom.Core.Hosting;
using Loom.Core.Materials;
using Silk.NET.Vulkan;
using VMASharp;
using VkFormat = Silk.NET.Vulkan.Format;

namespace Loom.Backend.Vulkan;

/// <summary>
/// Owns GPU-resident textures: device-local sampled images with a full mip chain, uploaded once via a
/// host-visible staging copy (the texture counterpart of <see cref="VulkanMeshStore"/>). The
/// app builds textures up front through the engine and gets back an opaque <see cref="Texture"/> handle; a
/// material draw resolves the handle to its image view and binds it as a combined image sampler. Mip levels
/// arrive pre-built on the CPU in <see cref="TextureData"/>, so the store just uploads each level — no GPU
/// blit/mip-gen, and byte-identical mips across backends. Images are freed on teardown or, when the app releases one,
/// once no frame in flight can read it.
/// </summary>
internal sealed unsafe class VulkanTextureStore : IDisposable
{
	private struct StoredTexture
	{
		public Image Image;
		public Allocation Allocation;
		public ImageView View;
		public bool Released; // the id's slot after IEngine.ReleaseTexture: no image, and Slot refuses it
	}

	private readonly VulkanContext _ctx;
	private readonly Vk _vk;
	private readonly VulkanMemoryAllocator _allocator;
	private readonly VulkanUploadBatch _uploads;
	private readonly List<StoredTexture> _textures = [];
	// Released textures whose images a frame in flight (or a pending upload) may still read, each with the last frame
	// that can.
	private readonly List<(StoredTexture Texture, ulong LastReadingFrame)> _releasing = [];

	public VulkanTextureStore(VulkanContext ctx, VulkanMemoryAllocator allocator, VulkanUploadBatch uploads)
	{
		_ctx = ctx;
		_vk = ctx.Vk;
		_allocator = allocator;
		_uploads = uploads;
	}

	/// <summary>
	/// Resolve a <see cref="Texture.Id"/> to its slot in the store. Ids are 1-based so <c>default(Texture)</c> (id 0) is
	/// never a live handle: sampling an unassigned <see cref="Texture"/> field fails here instead of silently binding
	/// whichever texture was uploaded first. A released texture keeps its slot, emptied, so its stale handle fails here
	/// too.
	/// </summary>
	private int Slot(int textureId)
	{
		if (textureId < 1 || textureId > _textures.Count)
		{
			throw new VulkanException(
				$"Texture id {textureId} was not created by this engine ({(textureId == 0 ? "0 is default(Texture), an unassigned handle" : $"only ids 1..{_textures.Count} exist")}). " +
				"Textures come from IEngine.CreateTexture / CreateTextureAsync.");
		}
		if (_textures[textureId - 1].Released)
			throw new VulkanException($"Texture id {textureId} was released by IEngine.ReleaseTexture; its handle can no longer be sampled or released.");
		return textureId - 1;
	}

	/// <summary>Upload a texture + its mip chain and return a handle a material can bundle.</summary>
	public Texture Create(TextureData data)
	{
		// Ids are never reused, and from DebugTextureBase up they name graph resources instead; refuse before allocating.
		if (_textures.Count + 1 >= InspectableResource.DebugTextureBase)
		{
			throw new VulkanException(
				$"This engine has minted {_textures.Count} texture ids, the most it can: ids are never reused, so that a released " +
				$"texture's stale handle fails rather than naming its successor, and ids from {InspectableResource.DebugTextureBase} " +
				"up name graph resources for the resource viewer.");
		}

		var format = data.Format switch
		{
			TextureFormat.Rgba8Srgb => VkFormat.R8G8B8A8Srgb,
			TextureFormat.Rgba8Unorm => VkFormat.R8G8B8A8Unorm,
			TextureFormat.Rgba16F => VkFormat.R16G16B16A16Sfloat,
			_ => throw new VulkanException($"No Vulkan texture format mapping for {data.Format}."),
		};
		// A cube map is six array layers created CUBE_COMPATIBLE and viewed as a cube; a 2D texture is one layer.
		// MipsPerFace is the per-layer mip count (== Levels.Count for a 2D image, since it has one layer).
		var mipLevels = (uint)data.MipsPerFace;
		var layerCount = (uint)data.LayerCount;

		// Device-local sampled image with the full mip chain.
		ImageCreateInfo imageInfo = new()
		{
			SType = StructureType.ImageCreateInfo,
			Flags = data.IsCube ? ImageCreateFlags.CreateCubeCompatibleBit : ImageCreateFlags.None,
			ImageType = ImageType.Type2D,
			Format = format,
			Extent = new Extent3D((uint)data.Width, (uint)data.Height, 1),
			MipLevels = mipLevels,
			ArrayLayers = layerCount,
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
			ViewType = data.IsCube ? ImageViewType.TypeCube : ImageViewType.Type2D,
			Format = format,
			SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, mipLevels, 0, layerCount),
		};
		_vk.CreateImageView(_ctx.Device, in viewInfo, null, out var view).Check("vkCreateImageView");

		UploadLevels(image, data);

		_textures.Add(new StoredTexture { Image = image, Allocation = allocation, View = view });
		return new Texture(_textures.Count, data.Width, data.Height);
	}

	/// <summary>The sampled image view a material binding points at.</summary>
	public ImageView View(int textureId) => _textures[Slot(textureId)].View;

	/// <summary>
	/// Release a texture: its id stops resolving at once, and its image waits until frame
	/// <paramref name="lastReadingFrame"/> has completed — a frame already submitted may still sample it, and the upload
	/// batch that fills it rides ahead of the next frame, so the caller names that next frame. Returns the view, which
	/// lives until then, so the caller can evict whatever is keyed by its handle before the driver can reuse it.
	/// </summary>
	public ImageView Release(int textureId, ulong lastReadingFrame)
	{
		var slot = Slot(textureId);
		var texture = _textures[slot];
		_releasing.Add((texture, lastReadingFrame));
		_textures[slot] = new StoredTexture { Released = true };
		return texture.View;
	}

	/// <summary>Destroy the image of every released texture whose last reading frame is <paramref name="completedFrame"/> or earlier.</summary>
	public void CollectReleased(ulong completedFrame)
	{
		for (var i = _releasing.Count - 1; i >= 0; i--)
		{
			if (_releasing[i].LastReadingFrame > completedFrame)
				continue;
			Destroy(_releasing[i].Texture);
			_releasing.RemoveAt(i);
		}
	}

	private void Destroy(in StoredTexture texture)
	{
		if (texture.View.Handle != 0)
			_vk.DestroyImageView(_ctx.Device, texture.View, null);
		if (texture.Image.Handle != 0)
			_vk.DestroyImage(_ctx.Device, texture.Image, null);
		texture.Allocation.Dispose();
	}

	// Stage every level (tightly packed, concatenated) in one host-visible buffer, transition the whole
	// mip/layer range Undefined→TransferDst, copy each level, then transition the whole range to ShaderReadOnly.
	// Levels are face-major (Levels[face*mipsPerFace + mip]); a 2D image is one layer, so the loop degenerates to
	// the plain mip chain.
	private void UploadLevels(Image image, TextureData data)
	{
		var mipsPerFace = (uint)data.MipsPerFace;
		var layerCount = (uint)data.LayerCount;

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
		var copies = new BufferImageCopy[data.Levels.Count];
		ulong offset = 0;
		for (var i = 0; i < data.Levels.Count; i++)
		{
			var level = data.Levels[i];
			level.Pixels.AsSpan().CopyTo(new Span<byte>(mapped + offset, level.Pixels.Length));
			copies[i] = new BufferImageCopy
			{
				BufferOffset = offset,
				ImageSubresource = new ImageSubresourceLayers
				{
					AspectMask = ImageAspectFlags.ColorBit,
					MipLevel = (uint)i % mipsPerFace,
					BaseArrayLayer = (uint)i / mipsPerFace,
					LayerCount = 1,
				},
				ImageExtent = new Extent3D((uint)level.Width, (uint)level.Height, 1),
			};
			offset += (ulong)level.Pixels.Length;
		}
		stagingAllocation.Unmap();

		// Recorded into the shared upload batch rather than a command buffer of its own: the transition to
		// ShaderReadOnly at the end is what orders this copy before any later-submitted draw that samples the
		// texture, so nothing has to wait here.
		var range = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, mipsPerFace, 0, layerCount);
		var cmd = _uploads.Recorder();
		TransitionImage(cmd, image, range,
			ImageLayout.Undefined, ImageLayout.TransferDstOptimal,
			PipelineStageFlags2.TopOfPipeBit, AccessFlags2.None,
			PipelineStageFlags2.AllTransferBit, AccessFlags2.TransferWriteBit);
		fixed (BufferImageCopy* pCopies = copies)
			_vk.CmdCopyBufferToImage(cmd, staging, image, ImageLayout.TransferDstOptimal, (uint)copies.Length, pCopies);
		TransitionImage(cmd, image, range,
			ImageLayout.TransferDstOptimal, ImageLayout.ShaderReadOnlyOptimal,
			PipelineStageFlags2.AllTransferBit, AccessFlags2.TransferWriteBit,
			PipelineStageFlags2.FragmentShaderBit, AccessFlags2.ShaderSampledReadBit);

		_uploads.Retire(staging, stagingAllocation);
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
			if (!texture.Released)
				Destroy(texture);
		}
		_textures.Clear();
		foreach (var (texture, _) in _releasing)
			Destroy(texture);
		_releasing.Clear();
	}
}
