using Loom.Graph;
using Silk.NET.Vulkan;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace Loom.Backend.Vulkan;

internal sealed unsafe partial class VulkanEngine
{
	// The layout/stage/access the present target rests in once a frame's work is done. Windowed: the real
	// PresentSrcKhr (VK_KHR_swapchain is enabled there, so the layout is valid and the presentation engine expects
	// it). Offscreen: VK_KHR_swapchain was never enabled — there is no window to present to — so PresentSrcKhr
	// isn't even a valid layout value there (the validation layer rejects it: "requires the extension
	// VK_KHR_swapchain"). Rest in ColorAttachmentOptimal instead; the next frame's first transition already treats
	// the image as Undefined (discarding prior contents), so the exact resting layout doesn't otherwise matter.
	private VulkanStateMapping.Scope PresentRestScope =>
		_offscreen ? VulkanStateMapping.For(ResourceState.ColorAttachment) : VulkanStateMapping.For(ResourceState.Present);

	// One frame's worth of pending barriers, grown on demand and reused — a pass's transitions are gathered here and
	// submitted together. Sized generously enough that a steady frame never grows them, because growing would
	// allocate on the hot path.
	private ImageMemoryBarrier2[] _imageBarriers = new ImageMemoryBarrier2[32];
	private BufferMemoryBarrier2[] _bufferBarriers = new BufferMemoryBarrier2[16];
	private int _imageBarrierCount;
	private int _bufferBarrierCount;

	/// <summary>
	/// Emit every transition in <paramref name="transitions"/> as a <em>single</em> pipeline barrier. A G-buffer
	/// hand-off is four or five transitions, and one barrier expressing all of them lets the driver resolve the
	/// stage/access dependency once instead of stalling five times over.
	/// <para>
	/// Batching is only sound because derivation folds a pass's accesses to one use per resource, so a pass never
	/// carries two transitions of the same resource — barriers inside one submission are unordered with respect to
	/// each other, and two on one resource would race.
	/// </para>
	/// </summary>
	private void EmitTransitions(CommandBuffer cmd, ReadOnlySpan<ResourceTransition> transitions)
	{
		_imageBarrierCount = 0;
		_bufferBarrierCount = 0;
		foreach (var transition in transitions)
			QueueTransition(transition);
		FlushBarriers(cmd);
	}

	private void QueueTransition(ResourceTransition transition)
	{
		var id = transition.Resource.Id;

		// Buffers have no layout — queue a buffer memory barrier scoped by the from/to stage+access. This is the
		// compute(StorageWrite) → vertex-input(VertexAttributeRead) hand-off for GPU particles.
		if (_bufferIds.Contains(id))
		{
			QueueBufferBarrier(_transients!.GetBuffer(_currentFrame, id), VulkanStateMapping.For(transition.From), VulkanStateMapping.For(transition.To));
			return;
		}

		ref var image = ref _transients!.Get(_currentFrame, id);

		// Persistent resources survive across frames. The barrier plan, derived per-frame, thinks every
		// resource starts Undefined; for a persistent one that would discard the value the next frame's compute
		// needs to read. Substitute the real layout/state the previous frame left it in (tracked here), so the
		// transition preserves contents. First frame ever: no entry yet, so it stays Undefined (garbage, which
		// the exposure adaptation converges away from). The single shared image + in-order submission make the
		// resulting SHADER_READ→GENERAL barrier the cross-frame read/write hazard guard.
		var fromState = transition.From;
		if (_persistentIds.Contains(id) && fromState == ResourceState.Undefined && _persistentState.TryGetValue(id, out var carried))
			fromState = carried;
		if (_persistentIds.Contains(id))
			_persistentState[id] = transition.To;

		var src = VulkanStateMapping.For(fromState);
		var dst = transition.To == ResourceState.Present ? PresentRestScope : VulkanStateMapping.For(transition.To);
		var srcStage = src.Stage;
		var srcAccess = src.Access;

		// Aliasing barrier: when a resource is born onto memory an earlier alias used this frame (an
		// Undefined→… transition for a non-first bucket member), the fresh-memory TopOfPipe source scope is
		// unsafe — the predecessor's GPU work on that memory may still be running. Widen the source scope to
		// "all prior commands / all memory" so this image's first write waits for the predecessor to finish.
		// (Conservative but correct; hand-offs are few. The Undefined old layout already discards the alias's
		// contents, which is exactly the aliasing semantic.)
		if (transition.From == ResourceState.Undefined && _aliasedHandoffIds.Contains(transition.Resource.Id))
		{
			srcStage = PipelineStageFlags2.AllCommandsBit;
			srcAccess = AccessFlags2.MemoryReadBit | AccessFlags2.MemoryWriteBit;
		}

		var range = new ImageSubresourceRange(image.Aspect, 0, 1, 0, 1);
		QueueImageBarrier(image.Image, range, src.Layout, dst.Layout, srcStage, srcAccess, dst.Stage, dst.Access);
	}

	private void QueueImageBarrier(
		Image image, ImageSubresourceRange range,
		ImageLayout oldLayout, ImageLayout newLayout,
		PipelineStageFlags2 srcStage, AccessFlags2 srcAccess,
		PipelineStageFlags2 dstStage, AccessFlags2 dstAccess)
	{
		if (_imageBarrierCount == _imageBarriers.Length)
			Array.Resize(ref _imageBarriers, _imageBarriers.Length * 2);

		_imageBarriers[_imageBarrierCount++] = new ImageMemoryBarrier2
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
	}

	// A buffer memory barrier (no layout) for the storage→vertex hand-off of a GPU-particle buffer.
	private void QueueBufferBarrier(Buffer buffer, VulkanStateMapping.Scope from, VulkanStateMapping.Scope to)
	{
		if (_bufferBarrierCount == _bufferBarriers.Length)
			Array.Resize(ref _bufferBarriers, _bufferBarriers.Length * 2);

		_bufferBarriers[_bufferBarrierCount++] = new BufferMemoryBarrier2
		{
			SType = StructureType.BufferMemoryBarrier2,
			SrcStageMask = from.Stage,
			SrcAccessMask = from.Access,
			DstStageMask = to.Stage,
			DstAccessMask = to.Access,
			SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
			DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
			Buffer = buffer,
			Offset = 0,
			Size = Vk.WholeSize,
		};
	}

	// Image and buffer barriers ride the same DependencyInfo: they describe one dependency between the same pair of
	// scopes, so splitting them into two submissions would be two stalls where the hardware needs one.
	private void FlushBarriers(CommandBuffer cmd)
	{
		if (_imageBarrierCount == 0 && _bufferBarrierCount == 0)
			return;

		fixed (ImageMemoryBarrier2* images = _imageBarriers)
		fixed (BufferMemoryBarrier2* buffers = _bufferBarriers)
		{
			DependencyInfo dependency = new()
			{
				SType = StructureType.DependencyInfo,
				ImageMemoryBarrierCount = (uint)_imageBarrierCount,
				PImageMemoryBarriers = _imageBarrierCount > 0 ? images : null,
				BufferMemoryBarrierCount = (uint)_bufferBarrierCount,
				PBufferMemoryBarriers = _bufferBarrierCount > 0 ? buffers : null,
			};
			_vk.CmdPipelineBarrier2(cmd, in dependency);
		}
		_imageBarrierCount = 0;
		_bufferBarrierCount = 0;
	}
}
