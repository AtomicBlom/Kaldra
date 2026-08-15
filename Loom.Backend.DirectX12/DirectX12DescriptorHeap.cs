using Loom.Core.Diagnostics;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D12;

namespace Loom.Backend.DirectX12;

/// <summary>
/// The shader-visible CBV/SRV/UAV heap: a persistent front region plus a per-frame bump allocator behind it.
/// <see cref="DirectX12RenderContext"/> bump-allocates a contiguous range per sampling draw, writes the input SRVs
/// there, and binds the table at the range's GPU handle. (DirectX12 has no per-descriptor binding — you bind a heap
/// once and index into it; this is the standard transient-descriptor pattern.)
/// <para>
/// <b>Two regions, because two lifetimes.</b> A material's texture table never changes — its textures are fixed at
/// creation — so re-writing it per draw is pure waste, and at ten thousand draws it is also the thing that used to
/// exhaust the heap. <see cref="AllocatePersistent"/> carves those tables out of a front region that is never reset;
/// everything genuinely per-frame (a pass's sampled inputs, a compute UAV table, a draw-list command's texture) comes
/// from <see cref="Allocate"/> behind it.
/// </para>
/// <para>
/// <b>Double-buffered per frame-in-flight.</b> The transient part is partitioned into <c>frameCount</c> equal regions
/// and a frame allocates only from its slot's region (<see cref="BeginFrame"/> takes the slot). This is load-bearing
/// with frames in flight: a single shared region reset to 0 every frame would let frame N+1's descriptor writes
/// clobber the descriptors frame N's still-executing GPU work is reading — invisible when consecutive frames are
/// identical, but a per-frame feedback corruption once content moves (it bit the bloom mip chain hardest, the most
/// descriptor-heavy graph). Each slot's region is only rewritten once its prior frame has retired (the per-frame fence).
/// </para>
/// <para>
/// <b>Growth, not refusal.</b> Running out grows the heap rather than throwing: a scene is allowed to get bigger than
/// whatever number was guessed at startup, and a fixed budget that fails loud on DirectX12 while Vulkan's pool chain
/// grows silently is a backend behavioural difference, not a design. Growth allocates a larger heap, restores the
/// persistent region into it, and re-binds — legal mid-command-list, and correct because the retired heap outlives the
/// draws that referenced it. It is reported, because a heap that keeps doubling is a leak and silence would hide it.
/// </para>
/// <para>
/// <b>Why a shadow heap.</b> Persistent descriptors are authored into a non-shader-visible heap and copied forward.
/// A shader-visible heap is CPU-write-only, so it cannot be a copy <em>source</em> — restoring the persistent region
/// after growth by copying out of the old heap is invalid, and the debug layer says so. The shadow heap is the
/// readable master copy; the shader-visible one only ever receives.
/// </para>
/// </summary>
internal sealed unsafe class DirectX12DescriptorHeap : IDisposable
{
	private readonly DirectX12Context _ctx;
	private readonly uint _frameCount;
	private readonly uint _increment;

	private ComPtr<ID3D12DescriptorHeap> _heap;    // shader-visible: what draws index into
	private ComPtr<ID3D12DescriptorHeap> _shadow;  // non-shader-visible master of the persistent region
	private uint _persistentCapacity;
	private uint _regionSize;       // transient descriptors per frame-in-flight slot
	private CpuDescriptorHandle _cpuStart;
	private GpuDescriptorHandle _gpuStart;
	private CpuDescriptorHandle _shadowStart;

	private uint _persistentNext;   // next free persistent descriptor (absolute index from the heap start)
	private uint _next;             // next free transient descriptor (absolute index)
	private uint _regionEnd;        // end of the current slot's transient region
	private uint _slot;

	// A heap the GPU may still be reading from. Released once every in-flight frame recorded against it has retired,
	// which the frame counter tracks — releasing at the growth site would free descriptors a queued frame still reads.
	private readonly List<(ComPtr<ID3D12DescriptorHeap> Heap, ulong Frame)> _retired = [];
	private ulong _frame;

	private ID3D12GraphicsCommandList* _cmd;

	public DirectX12DescriptorHeap(DirectX12Context ctx, uint persistentCapacity, uint transientPerFrame, uint frameCount)
	{
		_ctx = ctx;
		_frameCount = frameCount;
		_persistentCapacity = persistentCapacity;
		_regionSize = transientPerFrame;
		_increment = ctx.Device.GetDescriptorHandleIncrementSize(DescriptorHeapType.CbvSrvUav);
		_heap = CreateHeap(Capacity, shaderVisible: true);
		_shadow = CreateHeap(_persistentCapacity, shaderVisible: false);
		CacheStarts();
	}

	private uint Capacity => _persistentCapacity + (_regionSize * _frameCount);

	public ComPtr<ID3D12DescriptorHeap> Heap => _heap;

	/// <summary>
	/// Begin a frame on <paramref name="cmd"/>: bind the heap and rewind <paramref name="slot"/>'s own transient
	/// region, so this frame's descriptor writes never touch the descriptors another in-flight frame is still reading.
	/// The command list is retained so that a mid-frame growth can re-bind the new heap itself.
	/// </summary>
	public void BeginFrame(ID3D12GraphicsCommandList* cmd, uint slot)
	{
		_cmd = cmd;
		_slot = slot;
		_frame++;
		ReleaseRetired();
		BindHeap();
		RewindSlot();
	}

	/// <summary>
	/// Reserve <paramref name="count"/> contiguous descriptors for the lifetime of the engine and return the index of
	/// the range. Write the views through <see cref="PersistentCpu"/>, then publish them with
	/// <see cref="CommitPersistent"/>. Persistent ranges survive <see cref="BeginFrame"/> and survive growth, so a
	/// caller caches the index — not a handle, which growth would invalidate.
	/// </summary>
	public uint AllocatePersistent(uint count)
	{
		if (_persistentNext + count > _persistentCapacity)
			Grow(persistent: _persistentNext + count, transient: _regionSize);

		var index = _persistentNext;
		_persistentNext += count;
		return index;
	}

	/// <summary>The CPU handle to author persistent descriptor <paramref name="index"/> into — a slot in the readable
	/// shadow heap, not the shader-visible one.</summary>
	public CpuDescriptorHandle PersistentCpu(uint index) => new() { Ptr = _shadowStart.Ptr + (nuint)(index * _increment) };

	/// <summary>Publish a written persistent range into the shader-visible heap, where draws can index it.</summary>
	public void CommitPersistent(uint index, uint count) =>
		_ctx.Device.CopyDescriptorsSimple(count, CpuAt(index), PersistentCpu(index), DescriptorHeapType.CbvSrvUav);

	/// <summary>The GPU handle of persistent descriptor <paramref name="index"/>, to bind as a table. Resolved against
	/// the <em>current</em> heap, so a range allocated before a growth still binds correctly after it.</summary>
	public GpuDescriptorHandle PersistentGpu(uint index) => GpuAt(index);

	/// <summary>
	/// Make room for <paramref name="count"/> transient descriptors before any of them is bound. A draw that binds two
	/// descriptor tables must reserve both up front: growing between the two would leave the first table pointing into
	/// the retired heap while the new one is bound, which the debug layer rejects and the GPU reads as garbage.
	/// </summary>
	public void Reserve(uint count)
	{
		if (_next + count > _regionEnd)
			Grow(persistent: _persistentNext, transient: Math.Max(_regionSize * 2, (_next - SlotStart(_slot)) + count));
	}

	/// <summary>Reserve <paramref name="count"/> contiguous descriptors for this frame; returns the CPU handle (to write
	/// into) and GPU handle (to bind). Grows the heap rather than failing when this slot's region is exhausted.</summary>
	public (CpuDescriptorHandle Cpu, GpuDescriptorHandle Gpu) Allocate(uint count)
	{
		// Wrapping back into this slot's region is what must never happen: it would overwrite descriptors that an
		// earlier draw in this same command list still references at execute time — silent wrong pixels. Growing
		// gives the frame fresh descriptors instead, and the heap it outgrew stays alive until its frames retire.
		Reserve(count);
		var handles = (CpuAt(_next), GpuAt(_next));
		_next += count;
		return handles;
	}

	/// <summary>The CPU handle <paramref name="i"/> descriptors past <paramref name="handle"/>.</summary>
	public CpuDescriptorHandle Offset(CpuDescriptorHandle handle, uint i) => new() { Ptr = handle.Ptr + (nuint)(i * _increment) };

	private CpuDescriptorHandle CpuAt(uint index) => new() { Ptr = _cpuStart.Ptr + (nuint)(index * _increment) };
	private GpuDescriptorHandle GpuAt(uint index) => new() { Ptr = _gpuStart.Ptr + (ulong)(index * _increment) };

	private uint SlotStart(uint slot) => _persistentCapacity + (slot * _regionSize);

	private void RewindSlot()
	{
		_next = SlotStart(_slot);
		_regionEnd = _next + _regionSize;
	}

	private void BindHeap()
	{
		if (_cmd is null)
			return;
		var heap = _heap.Handle;
		_cmd->SetDescriptorHeaps(1, &heap);
	}

	// Move to a heap big enough for both regions. The persistent descriptors are restored from the shadow heap (their
	// indices are stable, which is why callers hold indices rather than handles); the transient region restarts,
	// because the new heap is untouched by any in-flight frame and this frame's already-recorded draws read the old.
	private void Grow(uint persistent, uint transient)
	{
		var oldCapacity = Capacity;
		var newPersistent = Math.Max(_persistentCapacity, RoundUpToPowerOfTwo(Math.Max(persistent, 1)));
		var newRegion = Math.Max(_regionSize, RoundUpToPowerOfTwo(Math.Max(transient, 1)));

		if (newPersistent != _persistentCapacity)
		{
			var shadow = CreateHeap(newPersistent, shaderVisible: false);
			if (_persistentNext > 0)
			{
				_ctx.Device.CopyDescriptorsSimple(
					_persistentNext, shadow.GetCPUDescriptorHandleForHeapStart(), _shadowStart, DescriptorHeapType.CbvSrvUav);
			}
			_shadow.Dispose(); // never referenced by the GPU, so it can go immediately
			_shadow = shadow;
			_persistentCapacity = newPersistent;
		}

		var grown = CreateHeap(newPersistent + (newRegion * _frameCount), shaderVisible: true);
		_retired.Add((_heap, _frame));
		_heap = grown;
		_regionSize = newRegion;
		CacheStarts();

		if (_persistentNext > 0)
			_ctx.Device.CopyDescriptorsSimple(_persistentNext, _cpuStart, _shadowStart, DescriptorHeapType.CbvSrvUav);

		BindHeap();
		RewindSlot();

		LoomLog.Info($"DirectX12 descriptor heap grew {oldCapacity} -> {Capacity} descriptors " +
			$"({_persistentNext}/{_persistentCapacity} persistent, {_regionSize} transient per frame slot).");
	}

	private ComPtr<ID3D12DescriptorHeap> CreateHeap(uint capacity, bool shaderVisible)
	{
		var desc = new DescriptorHeapDesc
		{
			Type = DescriptorHeapType.CbvSrvUav,
			NumDescriptors = capacity,
			Flags = shaderVisible ? DescriptorHeapFlags.ShaderVisible : DescriptorHeapFlags.None,
			NodeMask = 0
		};
		SilkMarshal.ThrowHResult(_ctx.Device.CreateDescriptorHeap(&desc, out ComPtr<ID3D12DescriptorHeap> heap));
		return heap;
	}

	private void CacheStarts()
	{
		_cpuStart = _heap.GetCPUDescriptorHandleForHeapStart();
		_gpuStart = _heap.GetGPUDescriptorHandleForHeapStart();
		_shadowStart = _shadow.GetCPUDescriptorHandleForHeapStart();
	}

	private void ReleaseRetired()
	{
		for (var i = _retired.Count - 1; i >= 0; i--)
		{
			if (_frame - _retired[i].Frame <= _frameCount)
				continue;
			_retired[i].Heap.Dispose();
			_retired.RemoveAt(i);
		}
	}

	private static uint RoundUpToPowerOfTwo(uint value)
	{
		var result = 1u;
		while (result < value)
			result <<= 1;
		return result;
	}

	public void Dispose()
	{
		foreach (var (heap, _) in _retired)
			heap.Dispose();
		_retired.Clear();
		_shadow.Dispose();
		_heap.Dispose();
	}
}
