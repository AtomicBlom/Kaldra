using Loom.Graph;
using Silk.NET.Vulkan;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace Loom.Vulkan;

internal sealed unsafe partial class VulkanEngine
{
	private void EmitTransition(CommandBuffer cmd, ResourceTransition transition)
	{
		var id = transition.Resource.Id;

		// Buffers have no layout — emit a buffer memory barrier scoped by the from/to stage+access. This is the
		// compute(StorageWrite) → vertex-input(VertexAttributeRead) hand-off for GPU particles.
		if (_bufferIds.Contains(id))
		{
			EmitBufferBarrier(cmd, _transients!.GetBuffer(_currentFrame, id), VulkanStateMapping.For(transition.From), VulkanStateMapping.For(transition.To));
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
		var dst = VulkanStateMapping.For(transition.To);
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
		TransitionImage(cmd, image.Image, range, src.Layout, dst.Layout, srcStage, srcAccess, dst.Stage, dst.Access);
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

	// A buffer memory barrier (no layout) for the storage→vertex hand-off of a GPU-particle buffer.
	private void EmitBufferBarrier(CommandBuffer cmd, Buffer buffer, VulkanStateMapping.Scope from, VulkanStateMapping.Scope to)
	{
		BufferMemoryBarrier2 barrier = new()
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
		DependencyInfo dependency = new()
		{
			SType = StructureType.DependencyInfo,
			BufferMemoryBarrierCount = 1,
			PBufferMemoryBarriers = &barrier,
		};
		_vk.CmdPipelineBarrier2(cmd, in dependency);
	}
}
