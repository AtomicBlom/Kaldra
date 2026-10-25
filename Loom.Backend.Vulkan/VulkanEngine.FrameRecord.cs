using System.Numerics;
using Loom.Core.Diagnostics;
using Loom.Core.Graph;
using Loom.Graph;
using Silk.NET.Vulkan;
using VMASharp;
using Buffer = Silk.NET.Vulkan.Buffer;
using VkFormat = Silk.NET.Vulkan.Format;

namespace Loom.Backend.Vulkan;

internal sealed unsafe partial class VulkanEngine
{
	private void DrawFrame(double deltaSeconds)
	{
		if (_windowExtent.Width == 0 || _windowExtent.Height == 0)
			return; // Minimized — nothing to draw.

		DrainShaderReloads(); // pick up any hot-reloaded shaders before recording this frame

		_vk.WaitForFences(_ctx.Device, 1, in _inFlight[_currentFrame], true, ulong.MaxValue);

		// Frames finish in submission order, so the frame this slot last carried being done means every earlier one
		// is too: free the released meshes nothing can still read.
		_completedFrame = Math.Max(_completedFrame, _slotFrame[_currentFrame]);
		_meshes.CollectReleased(_completedFrame);

		// The fence just signalled, so this slot's readback buffers hold the copies from when it last ran (~2
		// frames ago). Hand them to StoreReadback before this frame overwrites them — that's the readback
		// latency the design is upfront about. (Done before recording, so IEngine.Read always sees the freshest available.)
		DownloadReadback(_currentFrame);
		// Same story for the timing pool: the fence guarantees this slot's last timestamps are complete.
		DownloadTimings(_currentFrame);

		uint imageIndex;
		if (_offscreen)
		{
			// No presentation engine to acquire from — each frame-in-flight slot always renders into the same
			// owned image (VulkanOffscreenTarget sizes ImageCount to MaxFramesInFlight), so the slot index doubles
			// as the image index.
			imageIndex = (uint)_currentFrame;
		}
		else
		{
			imageIndex = 0;
			var acquire = _ctx.KhrSwapchain.AcquireNextImage(
				_ctx.Device, WindowSwapchain.Handle, ulong.MaxValue,
				_imageAvailable[_currentFrame], default, ref imageIndex);

			if (acquire == Result.ErrorOutOfDateKhr)
			{
				RecreatePresentTarget();
				return;
			}
			if (acquire is not (Result.Success or Result.SuboptimalKhr))
				acquire.Check("vkAcquireNextImageKHR");
		}

		_vk.ResetFences(_ctx.Device, 1, in _inFlight[_currentFrame]);

		var capturePath = TakeCapturePath(CaptureSupported(), _presentTarget.ImageFormat);
		var captureSize = (ulong)_presentTarget.Extent.Width * _presentTarget.Extent.Height * 4;
		Buffer captureBuffer = default;
		Allocation? captureAllocation = null;
		if (capturePath is not null)
			(captureBuffer, captureAllocation) = CreateStagingBuffer(captureSize);

		// This frame slot's prior work is done (fence waited), so its descriptor sets are idle: recycle them.
		_descriptors.BeginFrame(_currentFrame);
		_transients!.SetBackbuffer(_currentFrame, _presentTarget.Images[imageIndex], _presentTarget.ImageViews[imageIndex], _presentTarget.Extent);

		var cmd = _commandBuffers[_currentFrame];
		_vk.ResetCommandBuffer(cmd, 0);
		RecordCommandBuffer(cmd, captureBuffer, deltaSeconds);

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
		// Offscreen has no presentation engine to synchronise against — no image was acquired to wait on, and
		// nothing waits on this frame's completion — so the submit carries neither semaphore; the per-frame fence
		// alone paces it.
		SubmitInfo2 submit = new()
		{
			SType = StructureType.SubmitInfo2,
			WaitSemaphoreInfoCount = _offscreen ? 0u : 1u,
			PWaitSemaphoreInfos = _offscreen ? null : &waitInfo,
			CommandBufferInfoCount = 1,
			PCommandBufferInfos = &cmdInfo,
			SignalSemaphoreInfoCount = _offscreen ? 0u : 1u,
			PSignalSemaphoreInfos = _offscreen ? null : &signalInfo,
		};
		_slotFrame[_currentFrame] = ++_submittedFrames;
		_vk.QueueSubmit2(_ctx.GraphicsQueue, 1, in submit, _inFlight[_currentFrame]).Check("vkQueueSubmit2");

		if (capturePath is not null)
		{
			_vk.WaitForFences(_ctx.Device, 1, in _inFlight[_currentFrame], true, ulong.MaxValue);
			WriteCapture(captureAllocation!, captureSize, _presentTarget.Extent.Width, _presentTarget.Extent.Height, capturePath);
			_vk.DestroyBuffer(_ctx.Device, captureBuffer, null);
			captureAllocation!.Dispose();
			CapturePath = null;
		}

		if (_offscreen)
		{
			if (_framebufferResized)
			{
				_framebufferResized = false;
				RecreatePresentTarget();
			}
		}
		else
		{
			var swapchain = WindowSwapchain.Handle;
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
				RecreatePresentTarget();
			}
			else if (result != Result.Success)
			{
				result.Check("vkQueuePresentKHR");
			}
		}

		_currentFrame = (_currentFrame + 1) % MaxFramesInFlight;
	}

	// Open the command buffer, hand the context this frame's state, then let EngineBase's shared spine record the
	// derived plan through the hooks below.
	private void RecordCommandBuffer(CommandBuffer cmd, Buffer captureBuffer, double deltaTime)
	{
		CommandBufferBeginInfo beginInfo = new() { SType = StructureType.CommandBufferBeginInfo };
		_vk.BeginCommandBuffer(cmd, in beginInfo).Check("vkBeginCommandBuffer");

		_context.BeginFrame(cmd, deltaTime, _frameTime, _currentFrame, _presentTarget.Extent, _presentTarget.HdrActive);
		_captureBuffer = captureBuffer;

		RecordFrame();

		_vk.EndCommandBuffer(cmd).Check("vkEndCommandBuffer");
	}

	// The command buffer this frame is recording into — the one DrawFrame reset and handed to RecordCommandBuffer, which
	// is always this slot's. Read by the hooks rather than threaded through them, because the shared spine deals in
	// neutral terms and cannot carry a VkCommandBuffer.
	private CommandBuffer RecordingCommandBuffer => _commandBuffers[_currentFrame];

	// The staging buffer a requested capture copies the present target into, or a null handle. Held for FinishFrame.
	private Buffer _captureBuffer;

	// Whether this frame can collect timestamps at all: the pools only exist when the device reports valid timestamp
	// bits and the graph has passes to time.
	private bool TimingActive => GpuTimingEnabled && _timingPools.Length > 0;

	protected override void WriteConstantsShadow(int id, ReadOnlySpan<byte> bytes) =>
		_transients!.WriteConstants(_currentFrame, id, bytes);

	protected override void WriteStorageShadow(int id, ReadOnlySpan<byte> bytes) =>
		_transients!.WriteStorage(_currentFrame, id, bytes);

	protected override void EmitTransitions(ReadOnlySpan<ResourceTransition> transitions) =>
		EmitTransitions(RecordingCommandBuffer, transitions);

	protected override void RecordReadback() => RecordReadback(RecordingCommandBuffer);

	protected override void FinishFrame() => FinishFrame(RecordingCommandBuffer, _captureBuffer);

	// The reset is inside the command buffer, on the device timeline, so no hostQueryReset feature is needed.
	protected override void BeginFrameTimestamps()
	{
		if (TimingActive)
			_vk.CmdResetQueryPool(RecordingCommandBuffer, _timingPools[_currentFrame], 0, (uint)(2 * _timingPassCount));
	}

	// Top/Bottom-of-pipe is the portable choice for bracketing a pass; the device coarsens as needed.
	protected override void WritePassBeginTimestamp(int passIndex)
	{
		if (TimingActive)
			_vk.CmdWriteTimestamp2(RecordingCommandBuffer, PipelineStageFlags2.TopOfPipeBit, _timingPools[_currentFrame], (uint)(2 * passIndex));
	}

	protected override void WritePassEndTimestamp(int passIndex)
	{
		if (TimingActive)
			_vk.CmdWriteTimestamp2(RecordingCommandBuffer, PipelineStageFlags2.BottomOfPipeBit, _timingPools[_currentFrame], (uint)(2 * passIndex + 1));
	}

	protected override void EndFrameTimestamps()
	{
		if (TimingActive)
			_timingValid[_currentFrame] = true;
	}

	// The label scopes annotate the command buffer this frame is recording into — the same one RecordCommandBuffer was handed.
	protected override void BeginLabel(string name) => _ctx.BeginDebugLabel(_commandBuffers[_currentFrame], name);

	protected override void EndLabel() => _ctx.EndDebugLabel(_commandBuffers[_currentFrame]);

	// No attachments and no dynamic-rendering scope — just bind and dispatch. The resource transitions (storage
	// GENERAL, the consumer's SHADER_READ) were emitted before this.
	protected override void RecordComputePass(PassBarriers pass)
	{
		Span<int> uniformIds = stackalloc int[VulkanRenderContext.MaxUniformBuffers];
		var uniformCount = pass.Pass.ResourceIdsWith(AccessKind.ReadUniform, uniformIds);
		Span<int> storageIds = stackalloc int[VulkanRenderContext.MaxStorageBuffers];
		var storageCount = pass.Pass.ResourceIdsWith(AccessKind.ReadStorage, storageIds);
		_context.BeginPass(pass.Pass, [], default, false, uniformIds[..uniformCount], storageIds[..storageCount]); // no colour/depth targets; keeps the bake key stable
		pass.Pass.Record?.Invoke(_context);
	}

	protected override void RecordRasterPass(PassBarriers pass)
	{
		var cmd = RecordingCommandBuffer;

		// The app-writable parameter buffers this pass .Reads — bound to the technique's uniform slots 0..K-1.
		Span<int> uniformIds = stackalloc int[VulkanRenderContext.MaxUniformBuffers];
		var uniformCount = pass.Pass.ResourceIdsWith(AccessKind.ReadUniform, uniformIds);
		// The app-writable storage buffers this pass .Reads — bound to the technique's storage-buffer slots.
		Span<int> storageIds = stackalloc int[VulkanRenderContext.MaxStorageBuffers];
		var storageCount = pass.Pass.ResourceIdsWith(AccessKind.ReadStorage, storageIds);

		var attachments = pass.Attachments;
		var color = stackalloc RenderingAttachmentInfo[attachments.Length];
		var colorCount = 0;
		RenderingAttachmentInfo depth = default;
		var hasDepth = false;
		var depthReadOnly = false;
		Extent2D area = default;
		var colorFormats = stackalloc VkFormat[attachments.Length];
		VkFormat depthFormat = default;

		foreach (var attachment in attachments)
		{
			ref var image = ref _transients!.Get(_currentFrame, attachment.Resource.Id);
			if (area.Width == 0)
				area = image.Extent; // all of a pass's attachments share a domain → one render area

			if (attachment.State == ResourceState.ColorAttachment)
			{
				colorFormats[colorCount] = image.Format;
				color[colorCount++] = new RenderingAttachmentInfo
				{
					SType = StructureType.RenderingAttachmentInfo,
					ImageView = image.View,
					ImageLayout = ImageLayout.ColorAttachmentOptimal,
					LoadOp = ToLoadOp(attachment.Load),
					StoreOp = ToStoreOp(attachment.Store),
					ClearValue = ToVkClearColor(attachment.ClearValue),
				};
			}
			else
			{
				depthFormat = image.Format;
				depthReadOnly = attachment.State == ResourceState.DepthReadOnly;
				depth = new RenderingAttachmentInfo
				{
					SType = StructureType.RenderingAttachmentInfo,
					ImageView = image.View,
					ImageLayout = VulkanStateMapping.For(attachment.State).Layout,
					LoadOp = ToLoadOp(attachment.Load),
					StoreOp = ToStoreOp(attachment.Store),
					// The derived clear — 1 for a conventional depth target, 0 for a reversed-Z one. Ignored unless
					// LoadOp is Clear, in which case derivation always supplied a value.
					ClearValue = new ClearValue(depthStencil: new ClearDepthStencilValue(attachment.DepthClear ?? 1f, 0)),
				};
				hasDepth = true;
			}
		}

		RenderingInfo info = new()
		{
			SType = StructureType.RenderingInfo,
			RenderArea = new Rect2D(new Offset2D(0, 0), area),
			LayerCount = 1,
			ColorAttachmentCount = (uint)colorCount,
			PColorAttachments = colorCount > 0 ? color : null,
			PDepthAttachment = hasDepth ? &depth : null,
		};
		_vk.CmdBeginRendering(cmd, in info);

		Viewport viewport = new()
		{
			X = 0,
			Y = 0,
			Width = area.Width,
			Height = area.Height,
			MinDepth = 0,
			MaxDepth = 1,
		};
		_vk.CmdSetViewport(cmd, 0, 1, in viewport);
		Rect2D scissor = new(new Offset2D(0, 0), area);
		_vk.CmdSetScissor(cmd, 0, 1, in scissor);

		_context.BeginPass(pass.Pass, new ReadOnlySpan<VkFormat>(colorFormats, colorCount), depthFormat, depthReadOnly, uniformIds[..uniformCount], storageIds[..storageCount]);

		// Hand the context this pass's colour attachment views + area so a draw-list group-opacity layer can suspend
		// and resume the dynamic-rendering scope around its offscreen target.
		var scopeViews = stackalloc ImageView[colorCount];
		for (var i = 0; i < colorCount; i++)
			scopeViews[i] = color[i].ImageView;
		_context.SetColorScope(new ReadOnlySpan<ImageView>(scopeViews, colorCount), area);

		pass.Pass.Record?.Invoke(_context);

		_vk.CmdEndRendering(cmd);
	}

	// Terminal: hand the present target to the swapchain, copying it out first if a capture was requested.
	private void FinishFrame(CommandBuffer cmd, Buffer captureBuffer)
	{
		if (Graph!.PresentTarget is not { } present)
			return; // Headless graph: nothing to present.

		ref var image = ref _transients!.Get(_currentFrame, present.Id);
		var range = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1);

		if (captureBuffer.Handle != 0)
		{
			// The copy-out replaces the derived present transition, so it must start from the same place that
			// transition would have: the state the graph's last writer actually left the target in. Assuming
			// ColorAttachmentOptimal is wrong the moment the terminal pass isn't a colour write (a compute
			// post-process leaves it in General), and a wrong old layout discards the frame being captured.
			var from = VulkanStateMapping.For(PresentFromState(present));
			var copy = VulkanStateMapping.For(ResourceState.CopySource);
			// Queued and flushed on the spot, not batched: the copy below reads the image immediately.
			QueueImageBarrier(image.Image, range,
				from.Layout, copy.Layout,
				from.Stage, from.Access,
				copy.Stage, copy.Access);
			FlushBarriers(cmd);

			BufferImageCopy region = new()
			{
				ImageSubresource = new ImageSubresourceLayers { AspectMask = ImageAspectFlags.ColorBit, LayerCount = 1 },
				ImageExtent = new Extent3D(image.Extent.Width, image.Extent.Height, 1),
			};
			_vk.CmdCopyImageToBuffer(cmd, image.Image, copy.Layout, captureBuffer, 1, in region);

			var rest = PresentRestScope;
			QueueImageBarrier(image.Image, range,
				copy.Layout, rest.Layout,
				copy.Stage, copy.Access,
				rest.Stage, rest.Access);
			FlushBarriers(cmd);
		}
		else
		{
			EmitTransitions(cmd, Barriers.FinalTransitions.AsSpan());
		}
	}

	// The derived state the present target is in when the frame's passes are done — the From of its terminal
	// transition. Undefined if derivation emitted none (a graph that presents a target no pass writes).
	private ResourceState PresentFromState(ResourceHandle present)
	{
		foreach (var transition in Barriers.FinalTransitions)
			if (transition.Resource.Id == present.Id)
				return transition.From;
		return ResourceState.Undefined;
	}

	private static AttachmentLoadOp ToLoadOp(LoadAction load) => load switch
	{
		LoadAction.Clear => AttachmentLoadOp.Clear,
		LoadAction.Load => AttachmentLoadOp.Load,
		_ => AttachmentLoadOp.DontCare,
	};

	private static AttachmentStoreOp ToStoreOp(StoreAction store) =>
		store == StoreAction.Store ? AttachmentStoreOp.Store : AttachmentStoreOp.DontCare;

	private static ClearValue ToVkClearColor(Vector4? rgba) =>
		rgba is { } cv ? new ClearValue(color: new ClearColorValue(cv.X, cv.Y, cv.Z, cv.W)) : default;
}
