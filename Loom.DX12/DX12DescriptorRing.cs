using Silk.NET.Core.Native;
using Silk.NET.Direct3D12;

namespace Loom.DX12;

/// <summary>
/// A shader-visible CBV/SRV/UAV descriptor heap with a per-frame bump allocator — the staging ground for the
/// SRV descriptor tables a sampling pass binds. Each frame the engine <see cref="Reset"/>s it and binds it on
/// the command list; <see cref="DX12RenderContext"/> bump-allocates a contiguous range per sampling draw,
/// writes the input SRVs there, and binds the table at the range's GPU handle. (DX12 has no per-descriptor
/// binding — you bind a heap once and index into it; this is the standard transient-descriptor pattern.)
/// </summary>
internal sealed unsafe class DX12DescriptorRing : IDisposable
{
	private ComPtr<ID3D12DescriptorHeap> _heap;
	private readonly uint _capacity;
	private readonly uint _increment;
	private CpuDescriptorHandle _cpuStart;
	private GpuDescriptorHandle _gpuStart;
	private uint _next;

	public DX12DescriptorRing(DX12Context ctx, uint capacity)
	{
		_capacity = capacity;
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

	public void Reset() => _next = 0;

	/// <summary>Reserve <paramref name="count"/> contiguous descriptors; returns the CPU handle (to write into) and GPU handle (to bind).</summary>
	public (CpuDescriptorHandle Cpu, GpuDescriptorHandle Gpu) Allocate(uint count)
	{
		if (_next + count > _capacity)
			_next = 0; // wrap — our per-frame counts are tiny, so a frame never overruns a live range
		var cpu = new CpuDescriptorHandle { Ptr = _cpuStart.Ptr + (nuint)(_next * _increment) };
		var gpu = new GpuDescriptorHandle { Ptr = _gpuStart.Ptr + (ulong)(_next * _increment) };
		_next += count;
		return (cpu, gpu);
	}

	/// <summary>The CPU handle <paramref name="i"/> descriptors past <paramref name="handle"/>.</summary>
	public CpuDescriptorHandle Offset(CpuDescriptorHandle handle, uint i) => new() { Ptr = handle.Ptr + (nuint)(i * _increment) };

	public void Dispose() => _heap.Dispose();
}
