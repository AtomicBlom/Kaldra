using BinaryVibrance.Kaldra.Core.Diagnostics;
using Silk.NET.Vulkan;

namespace BinaryVibrance.Kaldra.Backend.Vulkan;

/// <summary>
/// Hands out short-lived descriptor sets for a frame's draws. One pool-chain per frame-in-flight, reset whole
/// at the start of that frame's turn (after its fence is known signalled), so the sets a frame allocates live
/// exactly as long as the command buffer that uses them. When a pool fills up a new one is appended and the
/// chain grows; extra pools are retained across frames so growth stabilises rather than thrashing. A proper
/// cached/bindless scheme is a later optimisation.
/// </summary>
internal sealed unsafe class VulkanDescriptorAllocator : IDisposable
{
	// The pool each live persistent set came from, by the set's handle: freeing one set on its own needs both, and the
	// cache that holds the set keeps only its handle.
	private readonly Dictionary<ulong, DescriptorPool> _setPools = [];

	private readonly VulkanContext _ctx;
	private readonly Vk _vk;
	private readonly List<DescriptorPool>[] _framePools;
	private readonly List<DescriptorPool> _persistentPools;
	private readonly uint _maxSetsPerFrame;
	private int _frameIndex;
	private int _poolIndex;
	private int _persistentIndex;
	// Persistent sets dropped from the cache that a frame in flight may still bind, each with its pool and the last frame
	// that can.
	private readonly List<(DescriptorSet Set, DescriptorPool Pool, ulong LastReadingFrame)> _retiring = [];

	public VulkanDescriptorAllocator(VulkanContext ctx, int framesInFlight, uint maxSetsPerFrame = 64)
	{
		_ctx = ctx;
		_vk = ctx.Vk;
		_maxSetsPerFrame = maxSetsPerFrame;
		_framePools = new List<DescriptorPool>[framesInFlight];
		for (var i = 0; i < framesInFlight; i++)
			_framePools[i] = [CreatePool(freeable: false)];
		_persistentPools = [CreatePool(freeable: true)];
	}

	// A persistent pool is created freeable because a cached set is occasionally evicted on its own — when something it
	// names is released between frames — rather than only ever with the whole chain at a reset.
	private DescriptorPool CreatePool(bool freeable)
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
			Flags = freeable ? DescriptorPoolCreateFlags.FreeDescriptorSetBit : DescriptorPoolCreateFlags.None,
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
	/// The caller is responsible for calling <see cref="ResetPersistent"/> whenever a whole generation of what cached
	/// sets name dies, and <see cref="RetirePersistent"/> for each set evicted on its own — see
	/// <see cref="BinaryVibrance.Kaldra.Graph.BindingCache"/>'s invariant.
	/// </para>
	/// </summary>
	public DescriptorSet AllocatePersistent(DescriptorSetLayout layout)
	{
		while (true)
		{
			var pool = _persistentPools[_persistentIndex];
			DescriptorSetAllocateInfo info = new()
			{
				SType = StructureType.DescriptorSetAllocateInfo,
				DescriptorPool = pool,
				DescriptorSetCount = 1,
				PSetLayouts = &layout,
			};
			var result = _vk.AllocateDescriptorSets(_ctx.Device, in info, out var set);
			// A freeable pool can also refuse for fragmentation once sets have been freed from it; either way the next
			// pool is the answer.
			if (result is not (Result.ErrorOutOfPoolMemory or Result.ErrorFragmentedPool))
			{
				result.Check("vkAllocateDescriptorSets");
				_setPools.Add(set.Handle, pool);
				return set;
			}
			_persistentIndex++;
			if (_persistentIndex >= _persistentPools.Count)
			{
				_persistentPools.Add(CreatePool(freeable: true));
				KaldraLog.Info($"Vulkan persistent descriptor pool chain grew to {_persistentPools.Count} pools " +
					$"({_maxSetsPerFrame} sets each).");
			}
		}
	}

	/// <summary>
	/// Free a persistent set evicted from the cache once frame <paramref name="lastReadingFrame"/> has completed. A frame
	/// already submitted may bind it, so it is freed on the same schedule as the object whose release evicted it.
	/// </summary>
	public void RetirePersistent(DescriptorSet set, ulong lastReadingFrame)
	{
		if (!_setPools.Remove(set.Handle, out var pool))
			throw new VulkanException($"Descriptor set 0x{set.Handle:X} is not a live persistent set, so it cannot be retired.");
		_retiring.Add((set, pool, lastReadingFrame));
	}

	/// <summary>Free every retired persistent set whose last reading frame is <paramref name="completedFrame"/> or earlier.</summary>
	public void CollectRetired(ulong completedFrame)
	{
		for (var i = _retiring.Count - 1; i >= 0; i--)
		{
			if (_retiring[i].LastReadingFrame > completedFrame)
				continue;
			var (set, pool, _) = _retiring[i];
			_vk.FreeDescriptorSets(_ctx.Device, pool, 1, in set).Check("vkFreeDescriptorSets");
			_retiring.RemoveAt(i);
		}
	}

	/// <summary>Free every persistently-allocated set by resetting the chain. The caller must have drained the GPU
	/// and dropped its cache first — resetting a pool invalidates the sets allocated from it, retired ones included.</summary>
	public void ResetPersistent()
	{
		foreach (var pool in _persistentPools)
			_vk.ResetDescriptorPool(_ctx.Device, pool, 0).Check("vkResetDescriptorPool");
		_persistentIndex = 0;
		_setPools.Clear();
		_retiring.Clear();
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
				_framePools[_frameIndex].Add(CreatePool(freeable: false));
				// Reported for the same reason DirectX12 reports its heap growth: growing is the right answer to a
				// scene that got bigger, and the wrong answer to a leak, and only a number distinguishes them.
				KaldraLog.Info($"Vulkan descriptor pool chain grew to {_framePools[_frameIndex].Count} pools " +
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
