using Silk.NET.Vulkan;
using VMASharp;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace BinaryVibrance.Kaldra.Backend.Vulkan;

/// <summary>
/// A per-frame-in-flight host-visible staging ring — where <c>ctx.Draw</c> with a <c>DrawCall.Instances</c> source
/// stages its per-instance data, and where per-draw and per-material uniform blocks land. Written map→copy→unmap
/// with no fence wait, because the executor waited the slot's fence before re-recording it.
/// <para>
/// A slot's storage is a <em>chain of blocks</em> rather than one buffer that grows. Blocks are allocated on demand
/// and then live until disposal: a frame that outgrows the current block moves on to the next instead of
/// reallocating, so <b>no <see cref="Buffer"/> handle is ever destroyed while the engine is running</b>. That is
/// load-bearing, not incidental — descriptor sets are cached by the buffer handles they name
/// (<c>VulkanRenderContext.Binding</c>), and a recycled handle would let a cache hit resolve to a different
/// allocation. The old design reallocated mid-frame and kept the previous buffer on a retirement list until the
/// slot came round again, which made every handle a temporary.
/// </para>
/// </summary>
internal sealed unsafe class VulkanInstanceUploadBuffer : IDisposable
{
	// A new chain starts modest and each further block doubles, capped, so a frame that needs a lot of staging
	// reaches capacity in a handful of allocations rather than dozens, without a small frame reserving megabytes.
	private const ulong FirstBlockBytes = 256 * 1024;
	private const ulong MaxBlockBytes = 8 * 1024 * 1024;

	private readonly VulkanContext _ctx;
	private readonly Vk _vk;
	private readonly VulkanMemoryAllocator _allocator;
	private readonly BufferUsageFlags _usage;
	private readonly ulong _alignment;
	private readonly List<Block>[] _blocks;
	private readonly int[] _blockIndex;
	private readonly ulong[] _cursors;

	private readonly record struct Block(Buffer Buffer, Allocation Allocation, ulong Size);

	/// <param name="ctx">The Vulkan device context owning these buffers.</param>
	/// <param name="allocator">The VMA allocator used to create and free the mappable buffers.</param>
	/// <param name="framesInFlight">Number of frames-in-flight slots to allocate (one block chain per slot).</param>
	/// <param name="usage">Buffer usage the ring's mappable buffers are created with — <see cref="BufferUsageFlags.VertexBufferBit"/>
	/// for the instance stream (the default), or <see cref="BufferUsageFlags.UniformBufferBit"/> for the uniform ring.</param>
	/// <param name="alignment">Per-append offset alignment — 16 for vertex streams; 256 for uniform buffers. 256 is
	/// ≥ any device's <c>minUniformBufferOffsetAlignment</c>, and every legal value of that limit is a power of two
	/// ≤ 256, so a 256-aligned offset satisfies it. The same reasoning covers the <c>pDynamicOffsets</c> alignment
	/// rule, which is the same limit — so a dynamic uniform binding can use these offsets directly.</param>
	public VulkanInstanceUploadBuffer(VulkanContext ctx, VulkanMemoryAllocator allocator, int framesInFlight,
		BufferUsageFlags usage = BufferUsageFlags.VertexBufferBit, ulong alignment = 16)
	{
		_ctx = ctx;
		_vk = ctx.Vk;
		_allocator = allocator;
		_usage = usage;
		_alignment = alignment;
		_blocks = new List<Block>[framesInFlight];
		_blockIndex = new int[framesInFlight];
		_cursors = new ulong[framesInFlight];
		for (var i = 0; i < framesInFlight; i++)
			_blocks[i] = [];
	}

	/// <summary>Start recording <paramref name="slot"/>: rewind to the head of its block chain. The frame's fence
	/// guarantees the GPU is done with what it wrote last time round, and the blocks themselves are reused as they
	/// are — nothing is freed, so nothing that references them can dangle.</summary>
	public void BeginFrame(int slot)
	{
		_blockIndex[slot] = 0;
		_cursors[slot] = 0;
	}

	/// <summary>Append <paramref name="data"/> to <paramref name="slot"/>'s chain; return the block's buffer plus the
	/// byte offset within it. An append that does not fit the current block starts the next one rather than growing
	/// this one, so the returned buffer may differ from the previous call's.</summary>
	public (Buffer Buffer, ulong Offset) Upload(int slot, ReadOnlySpan<byte> data)
	{
		var length = (ulong)data.Length;
		var offset = Align(_cursors[slot], _alignment);
		var block = Reserve(slot, offset, length, ref offset);

		var mapped = (byte*)block.Allocation.Map();
		data.CopyTo(new Span<byte>(mapped + offset, data.Length));
		block.Allocation.Unmap();
		_cursors[slot] = offset + length;
		return (block.Buffer, offset);
	}

	// Resolve the block this append lands in, moving to (or creating) the next one when it does not fit. Writes the
	// possibly-rewound offset back through `offset`.
	private Block Reserve(int slot, ulong wanted, ulong length, ref ulong offset)
	{
		var chain = _blocks[slot];
		var index = _blockIndex[slot];

		if (index < chain.Count && wanted + length <= chain[index].Size)
			return chain[index];

		// Move to the next block in the chain, allocating one if the chain has run out. Restart at offset 0: a fresh
		// block is already aligned, and abandoning the tail of the previous one costs at most one alignment window.
		index++;
		offset = 0;
		while (index < chain.Count && chain[index].Size < length)
			index++; // an existing block too small for this single append (only possible for an unusually large one)
		if (index >= chain.Count)
		{
			chain.Add(CreateBlock(BlockSizeFor(chain.Count, length)));
			index = chain.Count - 1;
		}
		_blockIndex[slot] = index;
		return chain[index];
	}

	// Doubling from FirstBlockBytes, capped — and never smaller than the append that triggered the allocation, so a
	// single oversized upload gets a block of its own rather than looping.
	private static ulong BlockSizeFor(int existingBlocks, ulong length)
	{
		var size = FirstBlockBytes;
		for (var i = 0; i < existingBlocks && size < MaxBlockBytes; i++)
			size *= 2;
		return Math.Max(Math.Min(size, MaxBlockBytes), length);
	}

	private Block CreateBlock(ulong size)
	{
		BufferCreateInfo info = new()
		{
			SType = StructureType.BufferCreateInfo,
			Size = size,
			Usage = _usage,
			SharingMode = SharingMode.Exclusive,
		};
		var buffer = _allocator.CreateBuffer(in info, new AllocationCreateInfo { Usage = MemoryUsage.CPU_Only }, out var allocation);
		return new Block(buffer, allocation, size);
	}

	private static ulong Align(ulong value, ulong alignment) => (value + alignment - 1) & ~(alignment - 1);

	public void Dispose()
	{
		foreach (var chain in _blocks)
		{
			foreach (var block in chain)
			{
				_vk.DestroyBuffer(_ctx.Device, block.Buffer, null);
				block.Allocation.Dispose();
			}
			chain.Clear();
		}
	}
}
