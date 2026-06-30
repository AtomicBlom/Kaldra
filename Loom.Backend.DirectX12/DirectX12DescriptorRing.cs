using Silk.NET.Core.Native;
using Silk.NET.Direct3D12;

namespace Loom.Backend.DirectX12;

/// <summary>
/// A shader-visible CBV/SRV/UAV descriptor heap with a per-frame bump allocator — the staging ground for the
/// SRV descriptor tables a sampling pass binds. <see cref="DirectX12RenderContext"/> bump-allocates a contiguous range
/// per sampling draw, writes the input SRVs there, and binds the table at the range's GPU handle. (DirectX12 has no
/// per-descriptor binding — you bind a heap once and index into it; this is the standard transient-descriptor pattern.)
/// <para>
/// <b>Double-buffered per frame-in-flight.</b> The heap is partitioned into <c>frameCount</c> equal regions and a
/// frame allocates only from its slot's region (<see cref="Reset"/> takes the slot). This is load-bearing with
/// frames in flight: a single shared region reset to 0 every frame would let frame N+1's descriptor writes clobber
/// the descriptors frame N's still-executing GPU work is reading — invisible when consecutive frames are identical,
/// but a per-frame feedback corruption once content moves (it bit the bloom mip chain hardest, the most
/// descriptor-heavy graph). Each slot's region is only rewritten once its prior frame has retired (the per-frame fence).
/// </para>
/// </summary>
internal sealed unsafe class DirectX12DescriptorRing : IDisposable
{
	private ComPtr<ID3D12DescriptorHeap> _heap;
	private readonly uint _capacity;
	private readonly uint _regionSize; // descriptors per frame-in-flight slot
	private readonly uint _increment;
	private CpuDescriptorHandle _cpuStart;
	private GpuDescriptorHandle _gpuStart;
	private uint _next;       // next free descriptor (absolute index into the heap)
	private uint _regionEnd;  // end of the current slot's region

	public DirectX12DescriptorRing(DirectX12Context ctx, uint capacity, uint frameCount)
	{
		_capacity = capacity;
		_regionSize = capacity / frameCount;
		var desc = new DescriptorHeapDesc
		{
			Type = DescriptorHeapType.CbvSrvUav,
			NumDescriptors = capacity,
			Flags = DescriptorHeapFlags.ShaderVisible,
			NodeMask = 0
		};
		SilkMarshal.ThrowHResult(ctx.Device.CreateDescriptorHeap(&desc, out _heap));
		_cpuStart = _heap.GetCPUDescriptorHandleForHeapStart();
		_gpuStart = _heap.GetGPUDescriptorHandleForHeapStart();
		_increment = ctx.Device.GetDescriptorHandleIncrementSize(DescriptorHeapType.CbvSrvUav);
	}

	public ComPtr<ID3D12DescriptorHeap> Heap => _heap;

	/// <summary>Begin a frame: bump-allocate from <paramref name="slot"/>'s own region, so this frame's descriptor
	/// writes never touch the descriptors another in-flight frame is still reading.</summary>
	public void Reset(uint slot)
	{
		_next = slot * _regionSize;
		_regionEnd = _next + _regionSize;
	}

	/// <summary>Reserve <paramref name="count"/> contiguous descriptors; returns the CPU handle (to write into) and GPU handle (to bind).</summary>
	public (CpuDescriptorHandle Cpu, GpuDescriptorHandle Gpu) Allocate(uint count)
	{
		// Fail loud on overflow. Wrapping back into this slot's region would overwrite descriptors that an
		// earlier draw recorded into this same command list still references at execute time — silent wrong
		// pixels. The heap must instead be sized for the worst-case per-frame descriptor budget at creation.
		if (_next + count > _regionEnd)
		{
			throw new InvalidOperationException(
				$"DirectX12 descriptor-ring overflow: this frame requested more than its per-slot budget of {_regionSize} " +
				$"descriptors. Increase the heap capacity (currently {_capacity} across all in-flight slots).");
		}
		var cpu = new CpuDescriptorHandle { Ptr = _cpuStart.Ptr + (nuint)(_next * _increment) };
		var gpu = new GpuDescriptorHandle { Ptr = _gpuStart.Ptr + (ulong)(_next * _increment) };
		_next += count;
		return (cpu, gpu);
	}

	/// <summary>The CPU handle <paramref name="i"/> descriptors past <paramref name="handle"/>.</summary>
	public CpuDescriptorHandle Offset(CpuDescriptorHandle handle, uint i) => new() { Ptr = handle.Ptr + (nuint)(i * _increment) };

	public void Dispose() => _heap.Dispose();
}
