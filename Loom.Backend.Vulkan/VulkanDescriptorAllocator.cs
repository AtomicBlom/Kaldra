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
	private readonly List<DescriptorPool> _persistentPools;
	private readonly uint _maxSetsPerFrame;
	private int _frameIndex;
	private int _poolIndex;
	private int _persistentIndex;

	public VulkanDescriptorAllocator(VulkanContext ctx, int framesInFlight, uint maxSetsPerFrame = 64)
	{
		_ctx = ctx;
		_vk = ctx.Vk;
		_maxSetsPerFrame = maxSetsPerFrame;
		_framePools = new List<DescriptorPool>[framesInFlight];
		for (var i = 0; i < framesInFlight; i++)
			_framePools[i] = [CreatePool()];
		_persistentPools = [CreatePool()];
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
			// Uniform buffers are bound with a dynamic offset, so the pool must stock that type and not the plain
			// one: a set layout's UNIFORM_BUFFER_DYNAMIC binding cannot be satisfied from a UNIFORM_BUFFER pool.
			Type = DescriptorType.UniformBufferDynamic,
			DescriptorCount = _maxSetsPerFrame * 4, // a technique may declare several (view-projection, material, per-draw)
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

	/// <summary>
	/// Allocate a set that outlives the frame, from a chain that is never reset. Sets handed out here back the
	/// descriptor-set cache: a material's images and samplers do not change between draws or between frames, so the
	/// set describing them is written once and re-bound with only its dynamic uniform offsets varying.
	/// <para>
	/// The caller is responsible for calling <see cref="ResetPersistent"/> whenever anything a cached set names can
	/// die — see the cache's own invariant comment.
	/// </para>
	/// </summary>
	public DescriptorSet AllocatePersistent(DescriptorSetLayout layout)
	{
		while (true)
		{
			DescriptorSetAllocateInfo info = new()
			{
				SType = StructureType.DescriptorSetAllocateInfo,
				DescriptorPool = _persistentPools[_persistentIndex],
				DescriptorSetCount = 1,
				PSetLayouts = &layout,
			};
			var result = _vk.AllocateDescriptorSets(_ctx.Device, in info, out var set);
			if (result != Result.ErrorOutOfPoolMemory)
			{
				result.Check("vkAllocateDescriptorSets");
				return set;
			}
			_persistentIndex++;
			if (_persistentIndex >= _persistentPools.Count)
			{
				_persistentPools.Add(CreatePool());
				LoomLog.Info($"Vulkan persistent descriptor pool chain grew to {_persistentPools.Count} pools " +
					$"({_maxSetsPerFrame} sets each).");
			}
		}
	}

	/// <summary>Free every persistently-allocated set by resetting the chain. The caller must have drained the GPU
	/// and dropped its cache first — resetting a pool invalidates the sets allocated from it.</summary>
	public void ResetPersistent()
	{
		foreach (var pool in _persistentPools)
			_vk.ResetDescriptorPool(_ctx.Device, pool, 0).Check("vkResetDescriptorPool");
		_persistentIndex = 0;
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
		foreach (var pool in _persistentPools)
			if (pool.Handle != 0)
				_vk.DestroyDescriptorPool(_ctx.Device, pool, null);
	}
}
