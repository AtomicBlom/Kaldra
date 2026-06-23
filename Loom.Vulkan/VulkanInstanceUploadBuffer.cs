using Silk.NET.Vulkan;
using VMASharp;

using Buffer = Silk.NET.Vulkan.Buffer;

namespace Loom.Vulkan;

/// <summary>
/// A per-frame-in-flight host-visible vertex buffer for CPU-driven instancing (M12) — the place
/// <c>ctx.DrawInstanced(mesh, technique, span)</c> stages its per-instance data each frame: one mappable buffer
/// per slot, grown with headroom, written map→copy→unmap, no fence wait (the executor waited the slot's fence
/// before re-recording it).
/// <para>
/// Multiple instanced draws in one frame append at successive (16-aligned) offsets into the slot's buffer. A
/// growth mid-frame preserves the bytes already written and <em>retires</em> the old buffer rather than freeing
/// it — a draw recorded earlier this frame still references it, so it is freed only at the slot's next
/// <see cref="BeginFrame"/>, after this frame's GPU work has completed.
/// </para>
/// </summary>
internal sealed unsafe class VulkanInstanceUploadBuffer : IDisposable
{
	private readonly VulkanContext _ctx;
	private readonly Vk _vk;
	private readonly VulkanMemoryAllocator _allocator;
	private readonly BufferUsageFlags _usage;
	private readonly ulong _alignment;
	private readonly Buffer[] _buffers;
	private readonly Allocation?[] _allocations;
	private readonly ulong[] _sizes;
	private readonly ulong[] _cursors;
	private readonly List<(Buffer Buffer, Allocation Allocation)>[] _retired;

	/// <param name="ctx">The Vulkan device context owning this buffer.</param>
	/// <param name="allocator">The VMA allocator used to create and free the mappable buffers.</param>
	/// <param name="framesInFlight">Number of frames-in-flight slots to allocate (one buffer per slot).</param>
	/// <param name="usage">Buffer usage the ring's mappable buffers are created with — <see cref="BufferUsageFlags.VertexBufferBit"/>
	/// for the instance stream (the default), or <see cref="BufferUsageFlags.UniformBufferBit"/> for the M15-0 uniform ring.</param>
	/// <param name="alignment">Per-append offset alignment — 16 for vertex streams; 256 for uniform buffers
	/// (≥ any device's <c>minUniformBufferOffsetAlignment</c>, which is a power of two ≤ 256, so 256 is always valid).</param>
	public VulkanInstanceUploadBuffer(VulkanContext ctx, VulkanMemoryAllocator allocator, int framesInFlight,
		BufferUsageFlags usage = BufferUsageFlags.VertexBufferBit, ulong alignment = 16)
	{
		_ctx = ctx;
		_vk = ctx.Vk;
		_allocator = allocator;
		_usage = usage;
		_alignment = alignment;
		_buffers = new Buffer[framesInFlight];
		_allocations = new Allocation?[framesInFlight];
		_sizes = new ulong[framesInFlight];
		_cursors = new ulong[framesInFlight];
		_retired = new List<(Buffer, Allocation)>[framesInFlight];
		for (var i = 0; i < framesInFlight; i++)
			_retired[i] = [];
	}

	/// <summary>Start recording <paramref name="slot"/>: free buffers retired while it was last recorded (its GPU work is done) and reset the write cursor.</summary>
	public void BeginFrame(int slot)
	{
		foreach (var (buffer, allocation) in _retired[slot])
		{
			_vk.DestroyBuffer(_ctx.Device, buffer, null);
			allocation.Dispose();
		}
		_retired[slot].Clear();
		_cursors[slot] = 0;
	}

	/// <summary>Append <paramref name="data"/> to <paramref name="slot"/>'s buffer; return the buffer plus the byte offset to bind the instance stream at.</summary>
	public (Buffer Buffer, ulong Offset) Upload(int slot, ReadOnlySpan<byte> data)
	{
		var offset = Align(_cursors[slot], _alignment);
		var needed = offset + (ulong)data.Length;
		EnsureCapacity(slot, needed);
		var mapped = (byte*)_allocations[slot]!.Map();
		data.CopyTo(new Span<byte>(mapped + offset, data.Length));
		_allocations[slot]!.Unmap();
		_cursors[slot] = needed;
		return (_buffers[slot], offset);
	}

	private void EnsureCapacity(int slot, ulong needed)
	{
		if (_sizes[slot] >= needed)
			return;

		var grown = needed + needed / 2; // 50% headroom so a frame's growth doesn't reallocate next frame
		BufferCreateInfo info = new()
		{
			SType = StructureType.BufferCreateInfo,
			Size = grown,
			Usage = _usage,
			SharingMode = SharingMode.Exclusive,
		};
		var newBuffer = _allocator.CreateBuffer(in info, new AllocationCreateInfo { Usage = MemoryUsage.CPU_Only }, out var newAllocation);

		// Preserve what's already been written this frame so earlier appends survive the grow.
		if (_buffers[slot].Handle != 0 && _cursors[slot] > 0)
		{
			var src = (byte*)_allocations[slot]!.Map();
			var dst = (byte*)newAllocation.Map();
			new Span<byte>(src, (int)_cursors[slot]).CopyTo(new Span<byte>(dst, (int)_cursors[slot]));
			_allocations[slot]!.Unmap();
			newAllocation.Unmap();
		}

		// A draw recorded earlier this frame may still reference the old buffer; retire (don't free) it.
		if (_buffers[slot].Handle != 0)
			_retired[slot].Add((_buffers[slot], _allocations[slot]!));

		_buffers[slot] = newBuffer;
		_allocations[slot] = newAllocation;
		_sizes[slot] = grown;
	}

	private static ulong Align(ulong value, ulong alignment) => (value + alignment - 1) & ~(alignment - 1);

	public void Dispose()
	{
		for (var slot = 0; slot < _buffers.Length; slot++)
		{
			foreach (var (buffer, allocation) in _retired[slot])
			{
				_vk.DestroyBuffer(_ctx.Device, buffer, null);
				allocation.Dispose();
			}
			_retired[slot].Clear();
			if (_buffers[slot].Handle != 0)
			{
				_vk.DestroyBuffer(_ctx.Device, _buffers[slot], null);
				_allocations[slot]!.Dispose();
			}
		}
	}
}
