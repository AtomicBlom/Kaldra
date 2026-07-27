using System.Diagnostics;
using System.Numerics;
using Loom.Core.Graph;
using Loom.Graph;
using Silk.NET.Vulkan;
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

		var capturePath = CapturePath;
		// PNG capture decodes an 8-bit RGBA/BGRA present target; an HDR swapchain (FP16 / 10-bit packed) would
		// decode to garbage, so skip it there rather than write a corrupt file (HDR is verified on-display).
		if (capturePath is not null && !CaptureSupported())
		{
			Console.WriteLine($"[loom] capture skipped: present format {_presentTarget.ImageFormat} is not an 8-bit RGBA/BGRA format (HDR output).");
			CapturePath = null;
			capturePath = null;
		}
		var captureSize = (ulong)_presentTarget.Extent.Width * _presentTarget.Extent.Height * 4;
		Buffer captureBuffer = default;
		DeviceMemory captureMemory = default;
		if (capturePath is not null)
			(captureBuffer, captureMemory) = CreateStagingBuffer(captureSize);

		// This frame slot's prior work is done (fence waited), so its descriptor sets are idle: recycle them.
		_descriptors.BeginFrame(_currentFrame);
		_transients!.SetBackbuffer(_currentFrame, _presentTarget.Images[imageIndex], _presentTarget.ImageViews[imageIndex], _presentTarget.Extent);

		var cmd = _commandBuffers[_currentFrame];
		_vk.ResetCommandBuffer(cmd, 0);
		RecordGraph(cmd, captureBuffer, deltaSeconds);

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
		_vk.QueueSubmit2(_ctx.GraphicsQueue, 1, in submit, _inFlight[_currentFrame]).Check("vkQueueSubmit2");

		if (capturePath is not null)
		{
			_vk.WaitForFences(_ctx.Device, 1, in _inFlight[_currentFrame], true, ulong.MaxValue);
			WriteCapture(captureMemory, captureSize, _presentTarget.Extent.Width, _presentTarget.Extent.Height, capturePath);
			_vk.DestroyBuffer(_ctx.Device, captureBuffer, null);
			_vk.FreeMemory(_ctx.Device, captureMemory, null);
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

	private void RecordGraph(CommandBuffer cmd, Buffer captureBuffer, double deltaTime)
	{
		CommandBufferBeginInfo beginInfo = new() { SType = StructureType.CommandBufferBeginInfo };
		_vk.BeginCommandBuffer(cmd, in beginInfo).Check("vkBeginCommandBuffer");

		_context.BeginFrame(cmd, deltaTime, _frameTime, _currentFrame, _presentTarget.Extent, _presentTarget.HdrActive);

		// Flush each parameter buffer's CPU shadow into this slot's copy before any pass reads it. Re-uploading
		// every frame (cheap — these are tiny) means the last IEngine.Write persists across frames, so a static
		// param is written once and an animated one is written per frame; either way the slot the GPU reads is current.
		foreach (var (id, bytes) in ConstantsShadows)
			_transients!.WriteConstants(_currentFrame, id, bytes);
		// Same for the app-writable storage buffers (IEngine.Write span).
		foreach (var (id, bytes) in StorageShadows)
			_transients!.WriteStorage(_currentFrame, id, bytes);

		// GPU timing: reset this slot's pool on the device timeline, then bracket each pass's own work with a
		// begin/end timestamp. The reset is inside the command buffer (no hostQueryReset feature needed).
		var timing = GpuTimingEnabled && _timingPools.Length > 0;
		if (timing)
			_vk.CmdResetQueryPool(cmd, _timingPools[_currentFrame], 0, (uint)(2 * _timingPassCount));

		var passIndex = 0;
		foreach (var pass in Barriers.Passes)
		{
			// Wrap each pass (its pre-barriers + its draws/dispatch) in a debug-label scope, nested under groups
			// derived from the dotted pass name — so a RenderDoc capture reads as a "bloom" group containing
			// "down1", "up0", … instead of a flat command list. [Conditional]: vanishes without UseNamedGPUObjects.
			BeginPassLabels(cmd, pass);
			foreach (var transition in pass.Transitions)
				EmitTransition(cmd, transition);
			// Timestamp the pass's own work (after its pre-barriers, around RecordPass) — the barriers belong to the
			// transition, not the pass. Top/Bottom-of-pipe is the portable choice; the device coarsens as needed.
			if (timing)
				_vk.CmdWriteTimestamp2(cmd, PipelineStageFlags2.TopOfPipeBit, _timingPools[_currentFrame], (uint)(2 * passIndex));
			RecordPass(cmd, pass);
			if (timing)
				_vk.CmdWriteTimestamp2(cmd, PipelineStageFlags2.BottomOfPipeBit, _timingPools[_currentFrame], (uint)(2 * passIndex + 1));
			_ctx.EndDebugLabel(cmd); // close the leaf scope opened by BeginPassLabels
			passIndex++;
		}
		if (timing)
			_timingValid[_currentFrame] = true;
		CloseTrailingLabels(cmd); // close any groups still open after the last pass

		RecordReadback(cmd);
		FinishFrame(cmd, captureBuffer);

		_vk.EndCommandBuffer(cmd).Check("vkEndCommandBuffer");
	}

	// Open this pass's debug-label scopes: close the groups it leaves behind, open the ones it enters, then open
	// its own leaf scope (closed by the caller after the pass records). [Conditional] so the whole thing — loops
	// and all — compiles away without UseNamedGPUObjects.
	[Conditional("LOOM_NAMED_GPU_OBJECTS")]
	private void BeginPassLabels(CommandBuffer cmd, PassBarriers pass)
	{
		for (var i = 0; i < pass.LabelCloses; i++)
			_ctx.EndDebugLabel(cmd);
		foreach (var group in pass.LabelOpens)
			_ctx.BeginDebugLabel(cmd, group);
		_ctx.BeginDebugLabel(cmd, pass.LeafLabel);
	}

	// Close the debug-label groups still open after the final pass (balances BeginPassLabels' opens).
	[Conditional("LOOM_NAMED_GPU_OBJECTS")]
	private void CloseTrailingLabels(CommandBuffer cmd)
	{
		for (var i = 0; i < Barriers.TrailingLabelCloses; i++)
			_ctx.EndDebugLabel(cmd);
	}

	private void RecordPass(CommandBuffer cmd, PassBarriers pass)
	{
		// The app-writable parameter buffers this pass .Reads — bound to the technique's uniform slots 0..K-1.
		Span<int> uniformIds = stackalloc int[VulkanRenderContext.MaxUniformBuffers];
		var uniformCount = CollectUniformIds(pass.Pass, uniformIds);
		// The app-writable storage buffers this pass .Reads — bound to the technique's storage-buffer slots.
		Span<int> storageIds = stackalloc int[VulkanRenderContext.MaxStorageBuffers];
		var storageCount = CollectStorageIds(pass.Pass, storageIds);

		// Compute passes have no attachments and no dynamic-rendering scope — just bind + dispatch. The
		// resource transitions (storage GENERAL, the consumer's SHADER_READ) were already emitted before this.
		if (pass.Pass.Kind == PassKind.Compute)
		{
			_context.BeginPass([], default, false, uniformIds[..uniformCount], storageIds[..storageCount]); // no colour/depth targets; keeps the bake key stable
			pass.Pass.Record?.Invoke(_context);
			return;
		}

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

		_context.BeginPass(new ReadOnlySpan<VkFormat>(colorFormats, colorCount), depthFormat, depthReadOnly, uniformIds[..uniformCount], storageIds[..storageCount]);

		// Hand the context this pass's colour attachment views + area so a draw-list group-opacity layer can suspend
		// and resume the dynamic-rendering scope around its offscreen target.
		var scopeViews = stackalloc ImageView[colorCount];
		for (var i = 0; i < colorCount; i++)
			scopeViews[i] = color[i].ImageView;
		_context.SetColorScope(new ReadOnlySpan<ImageView>(scopeViews, colorCount), area);

		pass.Pass.Record?.Invoke(_context);

		_vk.CmdEndRendering(cmd);
	}

	// Fill `ids` with the app-writable parameter buffers a pass .Reads (its ReadUniform edges, in .Reads order) —
	// they bind to the technique's uniform slots 0..; returns the count.
	private static int CollectUniformIds(GraphPass pass, Span<int> ids)
	{
		var n = 0;
		foreach (var access in pass.Accesses)
			if (access.Kind == AccessKind.ReadUniform && n < ids.Length)
				ids[n++] = access.Resource.Id;
		return n;
	}

	// Fill `ids` with the app-writable storage buffers a pass .Reads (its ReadStorage edges, in .Reads order) —
	// they bind to the technique's storage-buffer slots; returns the count.
	private static int CollectStorageIds(GraphPass pass, Span<int> ids)
	{
		var n = 0;
		foreach (var access in pass.Accesses)
			if (access.Kind == AccessKind.ReadStorage && n < ids.Length)
				ids[n++] = access.Resource.Id;
		return n;
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
			TransitionImage(cmd, image.Image, range,
				ImageLayout.ColorAttachmentOptimal, ImageLayout.TransferSrcOptimal,
				PipelineStageFlags2.ColorAttachmentOutputBit, AccessFlags2.ColorAttachmentWriteBit,
				PipelineStageFlags2.AllTransferBit, AccessFlags2.TransferReadBit);

			BufferImageCopy region = new()
			{
				ImageSubresource = new ImageSubresourceLayers { AspectMask = ImageAspectFlags.ColorBit, LayerCount = 1 },
				ImageExtent = new Extent3D(image.Extent.Width, image.Extent.Height, 1),
			};
			_vk.CmdCopyImageToBuffer(cmd, image.Image, ImageLayout.TransferSrcOptimal, captureBuffer, 1, in region);

			var rest = PresentRestScope;
			TransitionImage(cmd, image.Image, range,
				ImageLayout.TransferSrcOptimal, rest.Layout,
				PipelineStageFlags2.AllTransferBit, AccessFlags2.TransferReadBit,
				rest.Stage, rest.Access);
		}
		else
		{
			foreach (var transition in Barriers.FinalTransitions)
				EmitTransition(cmd, transition);
		}
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
