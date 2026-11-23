using Silk.NET.Core.Native;
using Silk.NET.Direct3D12;
using DxgiFormat = Silk.NET.DXGI.Format;

namespace BinaryVibrance.Kaldra.Backend.DirectX12;

/// <summary>
/// A per-frame-in-flight <c>UPLOAD</c>-heap vertex buffer for CPU-driven instancing — the DirectX12
/// counterpart of the Vulkan backend's <c>VulkanInstanceUploadBuffer</c>. <c>ctx.Draw</c> with a <c>DrawCall.Instances</c>
/// source stages its per-instance data here each frame; <see cref="Upload"/> returns the GPU virtual address to
/// point the instance <see cref="VertexBufferView"/> at.
/// <para>
/// Multiple instanced draws in one frame append at successive 16-aligned offsets. A growth mid-frame
/// preserves the bytes already written and <em>retires</em> the old buffer (a draw recorded earlier this
/// frame still references it), freeing it at the slot's next <see cref="BeginFrame"/> once its GPU work is done.
/// </para>
/// </summary>
internal sealed unsafe class DirectX12InstanceUploadBuffer : IDisposable
{
	private readonly DirectX12Context _ctx;
	private readonly ulong _alignment;
	private readonly ComPtr<ID3D12Resource>[] _buffers;
	private readonly ulong[] _sizes;
	private readonly ulong[] _cursors;
	private readonly List<ComPtr<ID3D12Resource>>[] _retired;

	/// <param name="ctx">The DirectX 12 device context owning this buffer.</param>
	/// <param name="frameCount">Number of frames-in-flight slots to allocate (one buffer per slot).</param>
	/// <param name="alignment">Per-append offset alignment — 16 for the instance stream (the default), or 256 for
	/// the uniform ring (D3D12 requires constant-buffer views 256-byte aligned).</param>
	public DirectX12InstanceUploadBuffer(DirectX12Context ctx, int frameCount, ulong alignment = 16)
	{
		_ctx = ctx;
		_alignment = alignment;
		_buffers = new ComPtr<ID3D12Resource>[frameCount];
		_sizes = new ulong[frameCount];
		_cursors = new ulong[frameCount];
		_retired = new List<ComPtr<ID3D12Resource>>[frameCount];
		for (var i = 0; i < frameCount; i++)
			_retired[i] = [];
	}

	/// <summary>Start recording <paramref name="slot"/>: free buffers retired while it was last recorded (its GPU work is done) and reset the write cursor.</summary>
	public void BeginFrame(uint slot)
	{
		foreach (var buffer in _retired[slot])
			buffer.Dispose();
		_retired[slot].Clear();
		_cursors[slot] = 0;
	}

	/// <summary>Append <paramref name="data"/> to <paramref name="slot"/>'s buffer; return the GPU virtual address to bind the instance stream at.</summary>
	public ulong Upload(uint slot, ReadOnlySpan<byte> data)
	{
		var offset = Align(_cursors[slot], _alignment);
		var needed = offset + (ulong)data.Length;
		EnsureCapacity(slot, needed);

		void* mapped;
		var noRead = new Silk.NET.Direct3D12.Range { Begin = 0, End = 0 };
		SilkMarshal.ThrowHResult(_buffers[slot].Map(0, &noRead, &mapped));
		data.CopyTo(new Span<byte>((byte*)mapped + offset, data.Length));
		_buffers[slot].Unmap(0, (Silk.NET.Direct3D12.Range*)null);

		_cursors[slot] = needed;
		return _buffers[slot].GetGPUVirtualAddress() + offset;
	}

	private void EnsureCapacity(uint slot, ulong needed)
	{
		if (_buffers[slot].Handle is not null && _sizes[slot] >= needed)
			return;

		var grown = needed + needed / 2; // 50% headroom
		var newBuffer = CreateUploadBuffer(grown);

		// Preserve what's already been written this frame so earlier appends survive the grow.
		if (_buffers[slot].Handle is not null && _cursors[slot] > 0)
		{
			void* src;
			void* dst;
			var noRead = new Silk.NET.Direct3D12.Range { Begin = 0, End = 0 };
			SilkMarshal.ThrowHResult(_buffers[slot].Map(0, &noRead, &src));
			SilkMarshal.ThrowHResult(newBuffer.Map(0, &noRead, &dst));
			new Span<byte>(src, (int)_cursors[slot]).CopyTo(new Span<byte>(dst, (int)_cursors[slot]));
			_buffers[slot].Unmap(0, (Silk.NET.Direct3D12.Range*)null);
			newBuffer.Unmap(0, (Silk.NET.Direct3D12.Range*)null);
		}

		// A draw recorded earlier this frame may still reference the old buffer; retire (don't free) it.
		if (_buffers[slot].Handle is not null)
			_retired[slot].Add(_buffers[slot]);

		_buffers[slot] = newBuffer;
		_sizes[slot] = grown;
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
		ComPtr<ID3D12Resource> buffer = default;
		SilkMarshal.ThrowHResult(_ctx.Device.CreateCommittedResource(
			&heapProps, HeapFlags.None, &desc, ResourceStates.GenericRead, (ClearValue*)null, out buffer));
		return buffer;
	}

	private static ulong Align(ulong value, ulong alignment) => (value + alignment - 1) & ~(alignment - 1);

	public void Dispose()
	{
		for (var slot = 0; slot < _buffers.Length; slot++)
		{
			foreach (var buffer in _retired[slot])
				buffer.Dispose();
			_retired[slot].Clear();
			if (_buffers[slot].Handle is not null)
				_buffers[slot].Dispose();
		}
	}
}
