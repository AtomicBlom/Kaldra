using Silk.NET.Maths;
using Silk.NET.Vulkan;

using Buffer = Silk.NET.Vulkan.Buffer;
using Semaphore = Silk.NET.Vulkan.Semaphore;

namespace Loom.Vulkan;

/// <summary>
/// M0 clear-to-color renderer: a command pool/buffers, frame-in-flight sync, and a per-frame
/// pass that clears the acquired swapchain image via <b>dynamic rendering</b> (no renderpass /
/// framebuffer objects) and presents it. Layout transitions use <c>vkCmdPipelineBarrier2</c>
/// (synchronization2). This is deliberately hand-written plumbing — the graph derives all of
/// this from M3 onward.
/// </summary>
public sealed unsafe class Renderer : IDisposable
{
	private const int MaxFramesInFlight = 2;

	private readonly VulkanContext _ctx;
	private readonly Vk _vk;
	private readonly Swapchain _swapchain;
	private readonly TechniqueCache _techniques;
	private readonly Technique _technique;

	private CommandPool _commandPool;
	private CommandBuffer[] _commandBuffers = [];
	private Semaphore[] _imageAvailable = []; // per frame-in-flight
	private Semaphore[] _renderFinished = []; // per swapchain image (avoids present-semaphore reuse)
	private Fence[] _inFlight = [];           // per frame-in-flight

	private int _currentFrame;
	private bool _framebufferResized;
	private Extent2D _windowExtent;
	private double _elapsed;
	private string? _capturePath;

	public Renderer(VulkanContext ctx, Vector2D<int> framebufferSize, string trianglePipelineSpv)
	{
		_ctx = ctx;
		_vk = ctx.Vk;
		_windowExtent = new Extent2D((uint)framebufferSize.X, (uint)framebufferSize.Y);
		_swapchain = new Swapchain(ctx, _windowExtent);
		_techniques = new TechniqueCache(ctx);
		_technique = _techniques.GetOrBake(
			new TechniqueDescription(trianglePipelineSpv, "vertexMain", "fragmentMain", _swapchain.ImageFormat));

		CreateCommandPool();
		CreateCommandBuffers();
		CreateSyncObjects();
	}

	public void OnResize(Vector2D<int> size)
	{
		_windowExtent = new Extent2D((uint)size.X, (uint)size.Y);
		_framebufferResized = true;
	}

	public void WaitIdle() => _vk.DeviceWaitIdle(_ctx.Device);

	/// <summary>Capture the next rendered frame to a PNG (debug/validation). Synchronous on that frame.</summary>
	public void RequestCapture(string path) => _capturePath = path;

	public void DrawFrame(double deltaSeconds)
	{
		if (_windowExtent.Width == 0 || _windowExtent.Height == 0)
			return; // Minimized — nothing to draw.

		_elapsed += deltaSeconds;

		_vk.WaitForFences(_ctx.Device, 1, in _inFlight[_currentFrame], true, ulong.MaxValue);

		uint imageIndex = 0;
		var acquire = _ctx.KhrSwapchain.AcquireNextImage(
			_ctx.Device, _swapchain.Handle, ulong.MaxValue,
			_imageAvailable[_currentFrame], default, ref imageIndex);

		if (acquire == Result.ErrorOutOfDateKhr)
		{
			RecreateSwapchain();
			return;
		}
		if (acquire is not (Result.Success or Result.SuboptimalKhr))
			acquire.Check("vkAcquireNextImageKHR");

		_vk.ResetFences(_ctx.Device, 1, in _inFlight[_currentFrame]);

		var capturePath = _capturePath;
		var captureSize = (ulong)_swapchain.Extent.Width * _swapchain.Extent.Height * 4;
		Buffer captureBuffer = default;
		DeviceMemory captureMemory = default;
		if (capturePath is not null)
			(captureBuffer, captureMemory) = CreateStagingBuffer(captureSize);

		var cmd = _commandBuffers[_currentFrame];
		_vk.ResetCommandBuffer(cmd, 0);
		RecordCommandBuffer(cmd, imageIndex, captureBuffer);

		SemaphoreSubmitInfo waitInfo = new()
		{
			SType = StructureType.SemaphoreSubmitInfo,
			Semaphore = _imageAvailable[_currentFrame],
			StageMask = PipelineStageFlags2.ColorAttachmentOutputBit,
		};
		SemaphoreSubmitInfo signalInfo = new()
		{
			SType = StructureType.SemaphoreSubmitInfo,
			Semaphore = _renderFinished[imageIndex],
			StageMask = PipelineStageFlags2.AllCommandsBit,
		};
		CommandBufferSubmitInfo cmdInfo = new()
		{
			SType = StructureType.CommandBufferSubmitInfo,
			CommandBuffer = cmd,
		};
		SubmitInfo2 submit = new()
		{
			SType = StructureType.SubmitInfo2,
			WaitSemaphoreInfoCount = 1,
			PWaitSemaphoreInfos = &waitInfo,
			CommandBufferInfoCount = 1,
			PCommandBufferInfos = &cmdInfo,
			SignalSemaphoreInfoCount = 1,
			PSignalSemaphoreInfos = &signalInfo,
		};
		_vk.QueueSubmit2(_ctx.GraphicsQueue, 1, in submit, _inFlight[_currentFrame]).Check("vkQueueSubmit2");

		if (capturePath is not null)
		{
			// Wait for the image->buffer copy to finish, then read it back and write the PNG.
			_vk.WaitForFences(_ctx.Device, 1, in _inFlight[_currentFrame], true, ulong.MaxValue);
			WriteCapture(captureMemory, captureSize, _swapchain.Extent.Width, _swapchain.Extent.Height, capturePath);
			_vk.DestroyBuffer(_ctx.Device, captureBuffer, null);
			_vk.FreeMemory(_ctx.Device, captureMemory, null);
			_capturePath = null;
		}

		var swapchain = _swapchain.Handle;
		var renderFinished = _renderFinished[imageIndex];
		var presentIndex = imageIndex;
		PresentInfoKHR present = new()
		{
			SType = StructureType.PresentInfoKhr,
			WaitSemaphoreCount = 1,
			PWaitSemaphores = &renderFinished,
			SwapchainCount = 1,
			PSwapchains = &swapchain,
			PImageIndices = &presentIndex,
		};
		var result = _ctx.KhrSwapchain.QueuePresent(_ctx.PresentQueue, in present);
		if (result is Result.ErrorOutOfDateKhr or Result.SuboptimalKhr || _framebufferResized)
		{
			_framebufferResized = false;
			RecreateSwapchain();
		}
		else if (result != Result.Success)
		{
			result.Check("vkQueuePresentKHR");
		}

		_currentFrame = (_currentFrame + 1) % MaxFramesInFlight;
	}

	private void RecordCommandBuffer(CommandBuffer cmd, uint imageIndex, Buffer captureBuffer)
	{
		CommandBufferBeginInfo beginInfo = new() { SType = StructureType.CommandBufferBeginInfo };
		_vk.BeginCommandBuffer(cmd, in beginInfo).Check("vkBeginCommandBuffer");

		var image = _swapchain.Images[imageIndex];
		var range = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1);

		// Acquired image is in UNDEFINED layout; transition it for color writes.
		TransitionImage(cmd, image, range,
			ImageLayout.Undefined, ImageLayout.ColorAttachmentOptimal,
			PipelineStageFlags2.TopOfPipeBit, AccessFlags2.None,
			PipelineStageFlags2.ColorAttachmentOutputBit, AccessFlags2.ColorAttachmentWriteBit);

		// Animate the clear colour so it's obvious the loop is live frame-to-frame.
		var t = (float)_elapsed;
		var clear = new ClearValue(color: new ClearColorValue(
			0.10f + 0.10f * MathF.Sin(t * 0.7f),
			0.10f + 0.10f * MathF.Sin(t * 0.9f + 2.0f),
			0.16f + 0.12f * MathF.Sin(t * 1.1f + 4.0f),
			1.0f));

		RenderingAttachmentInfo colorAttachment = new()
		{
			SType = StructureType.RenderingAttachmentInfo,
			ImageView = _swapchain.ImageViews[imageIndex],
			ImageLayout = ImageLayout.ColorAttachmentOptimal,
			LoadOp = AttachmentLoadOp.Clear,
			StoreOp = AttachmentStoreOp.Store,
			ClearValue = clear,
		};
		RenderingInfo renderingInfo = new()
		{
			SType = StructureType.RenderingInfo,
			RenderArea = new Rect2D(new Offset2D(0, 0), _swapchain.Extent),
			LayerCount = 1,
			ColorAttachmentCount = 1,
			PColorAttachments = &colorAttachment,
		};

		_vk.CmdBeginRendering(cmd, in renderingInfo);

		_vk.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, _technique.Pipeline);
		Viewport viewport = new()
		{
			X = 0,
			Y = 0,
			Width = _swapchain.Extent.Width,
			Height = _swapchain.Extent.Height,
			MinDepth = 0,
			MaxDepth = 1,
		};
		_vk.CmdSetViewport(cmd, 0, 1, in viewport);
		Rect2D scissor = new(new Offset2D(0, 0), _swapchain.Extent);
		_vk.CmdSetScissor(cmd, 0, 1, in scissor);
		_vk.CmdDraw(cmd, 3, 1, 0, 0); // fullscreen triangle: 3 fabricated verts, no vertex buffer

		_vk.CmdEndRendering(cmd);

		if (captureBuffer.Handle != 0)
		{
			// Copy the rendered image into the staging buffer, then make it presentable.
			TransitionImage(cmd, image, range,
				ImageLayout.ColorAttachmentOptimal, ImageLayout.TransferSrcOptimal,
				PipelineStageFlags2.ColorAttachmentOutputBit, AccessFlags2.ColorAttachmentWriteBit,
				PipelineStageFlags2.AllTransferBit, AccessFlags2.TransferReadBit);

			BufferImageCopy region = new()
			{
				ImageSubresource = new ImageSubresourceLayers
				{
					AspectMask = ImageAspectFlags.ColorBit,
					LayerCount = 1,
				},
				ImageExtent = new Extent3D(_swapchain.Extent.Width, _swapchain.Extent.Height, 1),
			};
			_vk.CmdCopyImageToBuffer(cmd, image, ImageLayout.TransferSrcOptimal, captureBuffer, 1, in region);

			TransitionImage(cmd, image, range,
				ImageLayout.TransferSrcOptimal, ImageLayout.PresentSrcKhr,
				PipelineStageFlags2.AllTransferBit, AccessFlags2.TransferReadBit,
				PipelineStageFlags2.BottomOfPipeBit, AccessFlags2.None);
		}
		else
		{
			// Transition for presentation.
			TransitionImage(cmd, image, range,
				ImageLayout.ColorAttachmentOptimal, ImageLayout.PresentSrcKhr,
				PipelineStageFlags2.ColorAttachmentOutputBit, AccessFlags2.ColorAttachmentWriteBit,
				PipelineStageFlags2.BottomOfPipeBit, AccessFlags2.None);
		}

		_vk.EndCommandBuffer(cmd).Check("vkEndCommandBuffer");
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

	private (Buffer Buffer, DeviceMemory Memory) CreateStagingBuffer(ulong size)
	{
		BufferCreateInfo info = new()
		{
			SType = StructureType.BufferCreateInfo,
			Size = size,
			Usage = BufferUsageFlags.TransferDstBit,
			SharingMode = SharingMode.Exclusive,
		};
		_vk.CreateBuffer(_ctx.Device, in info, null, out var buffer).Check("vkCreateBuffer");

		_vk.GetBufferMemoryRequirements(_ctx.Device, buffer, out var requirements);
		MemoryAllocateInfo alloc = new()
		{
			SType = StructureType.MemoryAllocateInfo,
			AllocationSize = requirements.Size,
			MemoryTypeIndex = FindMemoryType(
				requirements.MemoryTypeBits,
				MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit),
		};
		_vk.AllocateMemory(_ctx.Device, in alloc, null, out var memory).Check("vkAllocateMemory");
		_vk.BindBufferMemory(_ctx.Device, buffer, memory, 0).Check("vkBindBufferMemory");
		return (buffer, memory);
	}

	private uint FindMemoryType(uint typeBits, MemoryPropertyFlags required)
	{
		_vk.GetPhysicalDeviceMemoryProperties(_ctx.PhysicalDevice, out var properties);
		for (var i = 0u; i < properties.MemoryTypeCount; i++)
			if ((typeBits & (1u << (int)i)) != 0
				&& (properties.MemoryTypes[(int)i].PropertyFlags & required) == required)
				return i;
		throw new VulkanException("No host-visible memory type available for the capture buffer.");
	}

	private void WriteCapture(DeviceMemory memory, ulong size, uint width, uint height, string path)
	{
		void* mapped;
		_vk.MapMemory(_ctx.Device, memory, 0, size, 0, &mapped).Check("vkMapMemory");
		var src = new ReadOnlySpan<byte>(mapped, (int)size);

		// Swapchain is typically B8G8R8A8; PNG wants RGBA, so swap R/B for those formats.
		var swizzle = _swapchain.ImageFormat is Format.B8G8R8A8Srgb or Format.B8G8R8A8Unorm;
		var rgba = new byte[(int)size];
		for (var i = 0; i < (int)(width * height); i++)
		{
			var o = i * 4;
			rgba[o] = swizzle ? src[o + 2] : src[o];
			rgba[o + 1] = src[o + 1];
			rgba[o + 2] = swizzle ? src[o] : src[o + 2];
			rgba[o + 3] = src[o + 3];
		}
		_vk.UnmapMemory(_ctx.Device, memory);

		PngWriter.WriteRgba(path, (int)width, (int)height, rgba);
	}

	private void CreateCommandPool()
	{
		CommandPoolCreateInfo info = new()
		{
			SType = StructureType.CommandPoolCreateInfo,
			Flags = CommandPoolCreateFlags.ResetCommandBufferBit,
			QueueFamilyIndex = _ctx.GraphicsFamily,
		};
		_vk.CreateCommandPool(_ctx.Device, in info, null, out _commandPool).Check("vkCreateCommandPool");
	}

	private void CreateCommandBuffers()
	{
		_commandBuffers = new CommandBuffer[MaxFramesInFlight];
		CommandBufferAllocateInfo info = new()
		{
			SType = StructureType.CommandBufferAllocateInfo,
			CommandPool = _commandPool,
			Level = CommandBufferLevel.Primary,
			CommandBufferCount = MaxFramesInFlight,
		};
		fixed (CommandBuffer* p = _commandBuffers)
			_vk.AllocateCommandBuffers(_ctx.Device, in info, p).Check("vkAllocateCommandBuffers");
	}

	private void CreateSyncObjects()
	{
		_imageAvailable = new Semaphore[MaxFramesInFlight];
		_inFlight = new Fence[MaxFramesInFlight];

		SemaphoreCreateInfo semInfo = new() { SType = StructureType.SemaphoreCreateInfo };
		FenceCreateInfo fenceInfo = new() { SType = StructureType.FenceCreateInfo, Flags = FenceCreateFlags.SignaledBit };

		for (var i = 0; i < MaxFramesInFlight; i++)
		{
			_vk.CreateSemaphore(_ctx.Device, in semInfo, null, out _imageAvailable[i]).Check("vkCreateSemaphore");
			_vk.CreateFence(_ctx.Device, in fenceInfo, null, out _inFlight[i]).Check("vkCreateFence");
		}

		CreateRenderFinishedSemaphores();
	}

	private void CreateRenderFinishedSemaphores()
	{
		_renderFinished = new Semaphore[_swapchain.ImageCount];
		SemaphoreCreateInfo semInfo = new() { SType = StructureType.SemaphoreCreateInfo };
		for (var i = 0; i < _renderFinished.Length; i++)
			_vk.CreateSemaphore(_ctx.Device, in semInfo, null, out _renderFinished[i]).Check("vkCreateSemaphore");
	}

	private void RecreateSwapchain()
	{
		if (_windowExtent.Width == 0 || _windowExtent.Height == 0)
			return;

		_vk.DeviceWaitIdle(_ctx.Device);
		var oldImageCount = _swapchain.ImageCount;
		_swapchain.Recreate(_windowExtent);

		if (_swapchain.ImageCount != oldImageCount)
		{
			DestroyRenderFinishedSemaphores();
			CreateRenderFinishedSemaphores();
		}
	}

	private void DestroyRenderFinishedSemaphores()
	{
		foreach (var semaphore in _renderFinished)
			if (semaphore.Handle != 0)
				_vk.DestroySemaphore(_ctx.Device, semaphore, null);
		_renderFinished = [];
	}

	public void Dispose()
	{
		_vk.DeviceWaitIdle(_ctx.Device);

		_techniques.Dispose();
		DestroyRenderFinishedSemaphores();
		foreach (var semaphore in _imageAvailable)
			if (semaphore.Handle != 0)
				_vk.DestroySemaphore(_ctx.Device, semaphore, null);
		foreach (var fence in _inFlight)
			if (fence.Handle != 0)
				_vk.DestroyFence(_ctx.Device, fence, null);

		if (_commandPool.Handle != 0)
			_vk.DestroyCommandPool(_ctx.Device, _commandPool, null);

		_swapchain.Dispose();
	}
}
