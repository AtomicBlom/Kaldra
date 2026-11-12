using Loom.Core.Hosting;
using Silk.NET.Vulkan;
using VMASharp;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace Loom.Backend.Vulkan;

/// <summary>
/// Kept vertex and index buffers for draw-list streams that carry a stable <see cref="DrawListIdentity"/>, so a UI
/// surface whose geometry did not change is not re-uploaded every frame. A stream's buffers are written once per
/// revision and then simply re-bound.
/// <para>
/// <b>Host-visible, written map→copy→unmap, and that is forced rather than chosen.</b> <c>DrawList</c> is called
/// from inside a pass's record callback, which runs between <c>CmdBeginRendering</c> and <c>CmdEndRendering</c>, and
/// <c>vkCmdCopyBuffer</c> is illegal inside a dynamic-rendering scope. There is also no engine-side hook before the
/// pass walk where the producer's bytes exist — the shared record spine drains only the engine's own constants and
/// storage shadows. So the staged device-local upload the mesh store uses is not available here, and the right
/// shape is the existing ring minus the per-frame reset: same write path, kept instead of bump-allocated.
/// </para>
/// <para>
/// Keyed by <c>(stream, frame slot)</c>: with frames in flight the GPU may still be reading the slot that was
/// recorded last frame, so each slot needs its own copy. Writing the current slot is safe without any fence of our
/// own, because the executor already waited that slot's fence before re-recording it.
/// </para>
/// <para>
/// The store names <em>no graph resource</em>, so it needs no invalidation on resize or graph swap — unlike the
/// descriptor-set cache, nothing it holds can be destroyed underneath it. A stream that stops being submitted keeps
/// its buffers until <see cref="Dispose"/>; producers mint a bounded number of surfaces, so there is nothing here
/// worth a sweep.
/// </para>
/// </summary>
internal sealed unsafe class VulkanDrawListStore : IDisposable
{
	private readonly VulkanContext _ctx;
	private readonly Vk _vk;
	private readonly VulkanMemoryAllocator _allocator;
	private readonly Dictionary<(int Stream, int Slot), Kept> _kept = [];

	private sealed class Kept
	{
		public Buffer Vertex;
		public Buffer Index;
		public Allocation? VertexAllocation;
		public Allocation? IndexAllocation;
		public ulong VertexCapacity;
		public ulong IndexCapacity;
		public uint Revision;   // the revision the buffers currently hold; 0 = nothing written yet
		public long LastFrame = -1;
	}

	public VulkanDrawListStore(VulkanContext ctx, VulkanMemoryAllocator allocator)
	{
		_ctx = ctx;
		_vk = ctx.Vk;
		_allocator = allocator;
	}

	/// <summary>
	/// The buffers to bind for this submission, uploading <paramref name="vertices"/>/<paramref name="indices"/>
	/// only if the stream does not already hold <paramref name="revision"/>. Both are bound at offset 0 — a kept
	/// stream owns its buffers outright rather than a slice of a shared ring.
	/// </summary>
	public (Buffer Vertex, Buffer Index) Resolve(
		int stream, int slot, uint revision, long frameIndex,
		ReadOnlySpan<byte> vertices, ReadOnlySpan<byte> indices)
	{
		var key = (stream, slot);
		if (!_kept.TryGetValue(key, out var kept))
			_kept[key] = kept = new Kept();

		// A stream submitted again in one frame at the revision it holds re-binds the same bytes, as one UI drawn on the
		// frame and again into an inset does. At another revision the second write would land before the first draw
		// executes, serving both the second's geometry, so say so rather than render the wrong thing: the producer needs
		// distinct stream ids, or Transient.
		if (kept.LastFrame == frameIndex && kept.Revision != revision)
			throw new VulkanException(
				$"Draw-list stream {stream} was submitted twice in one frame, at revisions {kept.Revision} and {revision}. A " +
				"stable stream holds one kept buffer per frame slot, so the second submission would overwrite the first " +
				"before it draws. Give the submissions distinct stream ids, or pass DrawListIdentity.Transient.");
		kept.LastFrame = frameIndex;

		if (kept.Revision == revision && kept.VertexCapacity >= (ulong)vertices.Length && kept.IndexCapacity >= (ulong)indices.Length)
			return (kept.Vertex, kept.Index);

		Write(ref kept.Vertex, ref kept.VertexAllocation, ref kept.VertexCapacity, BufferUsageFlags.VertexBufferBit, vertices);
		Write(ref kept.Index, ref kept.IndexAllocation, ref kept.IndexCapacity, BufferUsageFlags.IndexBufferBit, indices);
		kept.Revision = revision;
		return (kept.Vertex, kept.Index);
	}

	// Grow if needed, then map/copy/unmap. Recreating is safe here for the same reason writing in place is: the
	// caller is recording this frame slot, whose fence the executor already waited, so nothing in flight names the
	// buffer being replaced.
	private void Write(ref Buffer buffer, ref Allocation? allocation, ref ulong capacity, BufferUsageFlags usage, ReadOnlySpan<byte> data)
	{
		var needed = (ulong)data.Length;
		if (capacity < needed)
		{
			if (buffer.Handle != 0)
			{
				_vk.DestroyBuffer(_ctx.Device, buffer, null);
				allocation!.Dispose();
			}
			var grown = needed + needed / 2; // headroom so a growing list does not reallocate every revision
			BufferCreateInfo info = new()
			{
				SType = StructureType.BufferCreateInfo,
				Size = grown,
				Usage = usage,
				SharingMode = SharingMode.Exclusive,
			};
			buffer = _allocator.CreateBuffer(in info, new AllocationCreateInfo { Usage = MemoryUsage.CPU_Only }, out var created);
			allocation = created;
			capacity = grown;
		}

		var mapped = (byte*)allocation!.Map();
		data.CopyTo(new Span<byte>(mapped, data.Length));
		allocation.Unmap();
	}

	public void Dispose()
	{
		foreach (var kept in _kept.Values)
		{
			if (kept.Vertex.Handle != 0)
			{
				_vk.DestroyBuffer(_ctx.Device, kept.Vertex, null);
				kept.VertexAllocation!.Dispose();
			}
			if (kept.Index.Handle != 0)
			{
				_vk.DestroyBuffer(_ctx.Device, kept.Index, null);
				kept.IndexAllocation!.Dispose();
			}
		}
		_kept.Clear();
	}
}
