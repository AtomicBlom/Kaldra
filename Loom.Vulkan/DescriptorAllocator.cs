using Silk.NET.Vulkan;

namespace Loom.Vulkan;

/// <summary>
/// Hands out short-lived descriptor sets for a frame's draws. One pool per frame-in-flight, reset whole at
/// the start of that frame's turn (after its fence is known signalled), so the sets a frame allocates live
/// exactly as long as the command buffer that uses them. The fullscreen-sample pass allocates one combined
/// image sampler set per invocation; the per-frame reset keeps that allocation-free of leaks without
/// tracking individual sets. A proper cached/bindless scheme is a later optimisation.
/// </summary>
internal sealed unsafe class DescriptorAllocator : IDisposable
{
	private readonly VulkanContext _ctx;
	private readonly Vk _vk;
	private readonly DescriptorPool[] _pools;
	private int _current;

	public DescriptorAllocator(VulkanContext ctx, int framesInFlight, uint maxSetsPerFrame = 64)
	{
		_ctx = ctx;
		_vk = ctx.Vk;
		_pools = new DescriptorPool[framesInFlight];

		DescriptorPoolSize poolSize = new()
		{
			Type = DescriptorType.CombinedImageSampler,
			DescriptorCount = maxSetsPerFrame,
		};
		DescriptorPoolCreateInfo info = new()
		{
			SType = StructureType.DescriptorPoolCreateInfo,
			MaxSets = maxSetsPerFrame,
			PoolSizeCount = 1,
			PPoolSizes = &poolSize,
		};
		for (var i = 0; i < framesInFlight; i++)
			_vk.CreateDescriptorPool(_ctx.Device, in info, null, out _pools[i]).Check("vkCreateDescriptorPool");
	}

	/// <summary>Recycle this frame slot's pool before recording. The frame's fence guarantees its sets are idle.</summary>
	public void BeginFrame(int frameIndex)
	{
		_current = frameIndex;
		_vk.ResetDescriptorPool(_ctx.Device, _pools[_current], 0).Check("vkResetDescriptorPool");
	}

	/// <summary>Allocate one descriptor set of the given layout from the current frame's pool.</summary>
	public DescriptorSet Allocate(DescriptorSetLayout layout)
	{
		DescriptorSetAllocateInfo info = new()
		{
			SType = StructureType.DescriptorSetAllocateInfo,
			DescriptorPool = _pools[_current],
			DescriptorSetCount = 1,
			PSetLayouts = &layout,
		};
		_vk.AllocateDescriptorSets(_ctx.Device, in info, out var set).Check("vkAllocateDescriptorSets");
		return set;
	}

	public void Dispose()
	{
		foreach (var pool in _pools)
			if (pool.Handle != 0)
				_vk.DestroyDescriptorPool(_ctx.Device, pool, null);
	}
}
