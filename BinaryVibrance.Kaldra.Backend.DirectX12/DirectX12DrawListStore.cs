using BinaryVibrance.Kaldra.Core.Hosting;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D12;
using DxgiFormat = Silk.NET.DXGI.Format;

namespace BinaryVibrance.Kaldra.Backend.DirectX12;

/// <summary>
/// Kept vertex and index buffers for draw-list streams that carry a stable <see cref="DrawListIdentity"/> — the
/// DirectX 12 counterpart of <c>VulkanDrawListStore</c>, so a UI surface whose geometry did not change is not
/// re-uploaded every frame.
/// <para>
/// <c>UPLOAD</c>-heap resources written through a plain map/copy/unmap, for the same reason the Vulkan store uses
/// host-visible memory: <c>DrawList</c> is called from inside a pass's record callback, so there is no legal place
/// to put a <c>CopyBufferRegion</c> into a <c>DEFAULT</c>-heap buffer. Reading vertices from UPLOAD is what the
/// per-frame ring already does.
/// </para>
/// <para>
/// Keyed by <c>(stream, frame slot)</c> because the GPU may still be reading the slot recorded last frame; writing
/// the current slot needs no fence of its own, because the executor waited that slot's fence before re-recording.
/// The store names no graph resource, so a resize or graph swap does not invalidate it.
/// </para>
/// </summary>
internal sealed unsafe class DirectX12DrawListStore : IDisposable
{
	private readonly DirectX12Context _ctx;
	private readonly Dictionary<(int Stream, int Slot), Kept> _kept = [];

	private sealed class Kept
	{
		public ComPtr<ID3D12Resource> Vertex;
		public ComPtr<ID3D12Resource> Index;
		public ulong VertexCapacity;
		public ulong IndexCapacity;
		public uint Revision;   // the revision the buffers currently hold; 0 = nothing written yet
		public long LastFrame = -1;
	}

	public DirectX12DrawListStore(DirectX12Context ctx) => _ctx = ctx;

	/// <summary>
	/// The GPU virtual addresses to point this submission's vertex and index views at, uploading only if the stream
	/// does not already hold <paramref name="revision"/>. Both address the start of a buffer the stream owns.
	/// </summary>
	public (ulong Vertex, ulong Index) Resolve(
		int stream, int slot, uint revision, long frameIndex,
		ReadOnlySpan<byte> vertices, ReadOnlySpan<byte> indices)
	{
		var key = (stream, slot);
		if (!_kept.TryGetValue(key, out var kept))
			_kept[key] = kept = new Kept();

		// A stream submitted again in one frame at the revision it holds re-binds the same bytes, as one UI drawn on the
		// frame and again into an inset does. One kept buffer per stream per slot, so at another revision the second
		// submission would overwrite the first before it draws: report it rather than render its geometry for both.
		if (kept.LastFrame == frameIndex && kept.Revision != revision)
			throw new InvalidOperationException(
				$"DirectX12: draw-list stream {stream} was submitted twice in one frame, at revisions {kept.Revision} and " +
				$"{revision}. A stable stream holds one kept buffer per frame slot, so the second submission would overwrite " +
				"the first before it draws. Give the submissions distinct stream ids, or pass DrawListIdentity.Transient.");
		kept.LastFrame = frameIndex;

		if (kept.Revision == revision && kept.VertexCapacity >= (ulong)vertices.Length && kept.IndexCapacity >= (ulong)indices.Length)
			return (kept.Vertex.GetGPUVirtualAddress(), kept.Index.GetGPUVirtualAddress());

		Write(ref kept.Vertex, ref kept.VertexCapacity, vertices);
		Write(ref kept.Index, ref kept.IndexCapacity, indices);
		kept.Revision = revision;
		return (kept.Vertex.GetGPUVirtualAddress(), kept.Index.GetGPUVirtualAddress());
	}

	// Grow if needed, then map/copy/unmap. Releasing in place is safe for the same reason writing in place is: the
	// caller is recording this frame slot, whose fence the executor already waited.
	private void Write(ref ComPtr<ID3D12Resource> buffer, ref ulong capacity, ReadOnlySpan<byte> data)
	{
		var needed = (ulong)data.Length;
		if (capacity < needed)
		{
			if (buffer.Handle is not null)
				buffer.Dispose();
			var grown = needed + needed / 2; // headroom so a growing list does not reallocate every revision
			buffer = CreateUploadBuffer(grown);
			capacity = grown;
		}

		void* mapped;
		var noRead = new Silk.NET.Direct3D12.Range { Begin = 0, End = 0 };
		SilkMarshal.ThrowHResult(buffer.Map(0, &noRead, &mapped));
		data.CopyTo(new Span<byte>(mapped, data.Length));
		buffer.Unmap(0, (Silk.NET.Direct3D12.Range*)null);
	}

	private ComPtr<ID3D12Resource> CreateUploadBuffer(ulong size)
	{
		var heapProps = new HeapProperties { Type = HeapType.Upload, CPUPageProperty = CpuPageProperty.Unknown, MemoryPoolPreference = MemoryPool.Unknown, CreationNodeMask = 1, VisibleNodeMask = 1 };
		var desc = new ResourceDesc
		{
			Dimension = ResourceDimension.Buffer,
			Alignment = 0,
			Width = size,
			Height = 1,
			DepthOrArraySize = 1,
			MipLevels = 1,
			Format = DxgiFormat.FormatUnknown,
			SampleDesc = new Silk.NET.DXGI.SampleDesc { Count = 1, Quality = 0 },
			Layout = TextureLayout.LayoutRowMajor,
			Flags = ResourceFlags.None,
		};
		SilkMarshal.ThrowHResult(_ctx.Device.CreateCommittedResource(
			&heapProps, HeapFlags.None, &desc, ResourceStates.GenericRead, (ClearValue*)null, out ComPtr<ID3D12Resource> buffer));
		return buffer;
	}

	public void Dispose()
	{
		foreach (var kept in _kept.Values)
		{
			if (kept.Vertex.Handle is not null)
				kept.Vertex.Dispose();
			if (kept.Index.Handle is not null)
				kept.Index.Dispose();
		}
		_kept.Clear();
	}
}
