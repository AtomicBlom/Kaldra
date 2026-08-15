using Loom.Core.Diagnostics;
using Silk.NET.Vulkan;

namespace Loom.Backend.Vulkan;

/// <summary>
/// Hands out short-lived descriptor sets for a frame's draws. One pool-chain per frame-in-flight, reset whole
/// at the start of that frame's turn (after its fence is known signalled), so the sets a frame allocates live
/// exactly as long as the command buffer that uses them. When a pool fills up a new one is appended and the
/// chain grows; extra pools are retained across frames so growth stabilises rather than thrashing. A proper
/// cached/bindless scheme is a later optimisation.
/// </summary>
internal sealed unsafe class VulkanDescriptorAllocator : IDisposable
{
	private readonly VulkanContext _ctx;
	private readonly Vk _vk;
	private readonly List<DescriptorPool>[] _framePools;
	private readonly uint _maxSetsPerFrame;
	private int _frameIndex;
	private int _poolIndex;

	public VulkanDescriptorAllocator(VulkanContext ctx, int framesInFlight, uint maxSetsPerFrame = 64)
	{
		_ctx = ctx;
		_vk = ctx.Vk;
		_maxSetsPerFrame = maxSetsPerFrame;
		_framePools = new List<DescriptorPool>[framesInFlight];
		for (var i = 0; i < framesInFlight; i++)
			_framePools[i] = [CreatePool()];
	}

	private DescriptorPool CreatePool()
	{
		var poolSizes = stackalloc DescriptorPoolSize[4];
		poolSizes[0] = new DescriptorPoolSize
		{
			Type = DescriptorType.CombinedImageSampler,
			DescriptorCount = _maxSetsPerFrame * 8, // sets may bind several samplers (e.g. deferred lighting)
		};
		poolSizes[1] = new DescriptorPoolSize
		{
			Type = DescriptorType.StorageImage,
			DescriptorCount = _maxSetsPerFrame * 2, // compute passes bind a storage image or two (auto-exposure)
		};
		poolSizes[2] = new DescriptorPoolSize
		{
			Type = DescriptorType.StorageBuffer,
			DescriptorCount = _maxSetsPerFrame * 4, // compute passes bind storage buffers (GPU particles, skinning)
		};
		poolSizes[3] = new DescriptorPoolSize
		{
			Type = DescriptorType.UniformBuffer,
			DescriptorCount = _maxSetsPerFrame * 2, // a pass may bind one per-frame uniform buffer (CSM, lighting, wind)
		};
		DescriptorPoolCreateInfo info = new()
		{
			SType = StructureType.DescriptorPoolCreateInfo,
			MaxSets = _maxSetsPerFrame,
			PoolSizeCount = 4,
			PPoolSizes = poolSizes,
		};
		_vk.CreateDescriptorPool(_ctx.Device, in info, null, out var pool).Check("vkCreateDescriptorPool");
		return pool;
	}

	/// <summary>Recycle this frame slot's pool chain before recording. The frame's fence guarantees its sets are idle.</summary>
	public void BeginFrame(int frameIndex)
	{
		_frameIndex = frameIndex;
		_poolIndex = 0;
		foreach (var pool in _framePools[_frameIndex])
			_vk.ResetDescriptorPool(_ctx.Device, pool, 0).Check("vkResetDescriptorPool");
	}

	/// <summary>Allocate one descriptor set of the given layout from the current frame's pool chain, growing it if needed.</summary>
	public DescriptorSet Allocate(DescriptorSetLayout layout)
	{
		while (true)
		{
			DescriptorSetAllocateInfo info = new()
			{
				SType = StructureType.DescriptorSetAllocateInfo,
				DescriptorPool = _framePools[_frameIndex][_poolIndex],
				DescriptorSetCount = 1,
				PSetLayouts = &layout,
			};
			var result = _vk.AllocateDescriptorSets(_ctx.Device, in info, out var set);
			if (result != Result.ErrorOutOfPoolMemory)
			{
				result.Check("vkAllocateDescriptorSets");
				return set;
			}
			_poolIndex++;
			if (_poolIndex >= _framePools[_frameIndex].Count)
			{
				_framePools[_frameIndex].Add(CreatePool());
				// Reported for the same reason DirectX12 reports its heap growth: growing is the right answer to a
				// scene that got bigger, and the wrong answer to a leak, and only a number distinguishes them.
				LoomLog.Info($"Vulkan descriptor pool chain grew to {_framePools[_frameIndex].Count} pools " +
					$"for frame slot {_frameIndex} ({_maxSetsPerFrame} sets each).");
			}
		}
	}

	public void Dispose()
	{
		foreach (var framePool in _framePools)
			foreach (var pool in framePool)
				if (pool.Handle != 0)
					_vk.DestroyDescriptorPool(_ctx.Device, pool, null);
	}
}
