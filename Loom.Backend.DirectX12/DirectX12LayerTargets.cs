using Loom.Core.Hosting;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D12;
using DxgiFormat = Silk.NET.DXGI.Format;

namespace Loom.Backend.DirectX12;

/// <summary>
/// The renderer-private offscreen targets backing UI group-opacity layers (the <c>BeginLayer</c>/<c>EndLayer</c>
/// markers a <c>ctx.DrawList</c> submission carries) — the DirectX12 twin of <c>VulkanLayerTargets</c>. These are
/// <em>not</em> graph resources: they live below the graph seam so the render graph still sees one <c>ui</c> pass and
/// its hash never moves. One full-surface render target is pooled per (frame slot, nesting depth); nested layers need
/// distinct targets at once, while sibling layers at the same depth reuse one (the render-target↔shader-resource
/// transitions order the prior composite read before the next clear). Targets are kept across frames (retired, not
/// freed) and recreated when the surface size or colour format changes. Pooling at the pass colour format lets the
/// already-baked <c>ui</c> pipeline render into a layer unchanged.
/// </summary>
internal sealed unsafe class DirectX12LayerTargets : IDisposable
{
	// A practical ceiling on UI group nesting; the RTV heap is sized for this many targets per frame slot.
	// Sized from the neutral cap the caller enforces, not a number of its own: the pool is indexed by nesting depth, so
	// a pool smaller than the cap is an out-of-range write and a pool larger is memory nothing can reach.
	private const int MaxDepth = DrawListGeometry.MaxLayerNesting;

	private readonly DirectX12Context _ctx;
	private readonly int _slots;

	internal struct Target
	{
		public ComPtr<ID3D12Resource> Resource;
		public CpuDescriptorHandle Rtv;
		public ResourceStates State;
		public bool Created;
	}

	private ComPtr<ID3D12DescriptorHeap> _rtvHeap;
	private uint _rtvInc;
	private readonly Target[][] _pools; // [slot][depth]
	private uint _width, _height;
	private DxgiFormat _format;

	public DirectX12LayerTargets(DirectX12Context ctx, int frameCount)
	{
		_ctx = ctx;
		_slots = frameCount;
		_rtvInc = ctx.Device.GetDescriptorHandleIncrementSize(DescriptorHeapType.Rtv);
		var desc = new DescriptorHeapDesc { Type = DescriptorHeapType.Rtv, NumDescriptors = (uint)(frameCount * MaxDepth), Flags = DescriptorHeapFlags.None, NodeMask = 0 };
		SilkMarshal.ThrowHResult(ctx.Device.CreateDescriptorHeap(&desc, out _rtvHeap));
		_pools = new Target[frameCount][];
		for (var i = 0; i < frameCount; i++)
			_pools[i] = new Target[MaxDepth];
	}

	/// <summary>The colour format the layer targets are rendered/sampled at (the pass colour format).</summary>
	public DxgiFormat Format => _format;

	/// <summary>
	/// Bind the pool to the current surface size + colour format. A change (resize, HDR toggle) releases every pooled
	/// target — they would no longer match the resumed pass attachment. A no-op when unchanged.
	/// </summary>
	public void Configure(uint width, uint height, DxgiFormat format)
	{
		if (_width == width && _height == height && _format == format)
			return;
		ReleaseTargets();
		_width = width;
		_height = height;
		_format = format;
	}

	/// <summary>Get (creating on first use) the layer target for <paramref name="slot"/> at nesting <paramref name="depth"/>.</summary>
	public ref Target Acquire(uint slot, int depth)
	{
		ref var t = ref _pools[slot][depth];
		if (!t.Created)
			Create(ref t, slot, depth);
		return ref t;
	}

	private void Create(ref Target t, uint slot, int depth)
	{
		var heapProps = new HeapProperties { Type = HeapType.Default, CPUPageProperty = CpuPageProperty.Unknown, MemoryPoolPreference = MemoryPool.Unknown, CreationNodeMask = 1, VisibleNodeMask = 1 };
		var desc = new ResourceDesc
		{
			Dimension = ResourceDimension.Texture2D,
			Alignment = 0,
			Width = _width,
			Height = _height,
			DepthOrArraySize = 1,
			MipLevels = 1,
			Format = _format,
			SampleDesc = new Silk.NET.DXGI.SampleDesc { Count = 1, Quality = 0 },
			Layout = TextureLayout.LayoutUnknown,
			Flags = ResourceFlags.AllowRenderTarget,
		};
		// Optimised clear = transparent black (the layer starts empty), matching the ClearRenderTargetView the caller
		// issues, so the debug layer doesn't warn.
		var clear = new ClearValue { Format = _format };
		clear.Anonymous.Color[0] = 0f;
		clear.Anonymous.Color[1] = 0f;
		clear.Anonymous.Color[2] = 0f;
		clear.Anonymous.Color[3] = 0f;
		ComPtr<ID3D12Resource> resource = default;
		SilkMarshal.ThrowHResult(_ctx.Device.CreateCommittedResource(
			&heapProps, HeapFlags.None, &desc, ResourceStates.RenderTarget, &clear, out resource));
		_ctx.SetObjectName((ID3D12Object*)resource.Handle, $"ui.layer#{slot}.{depth}");

		var rtv = new CpuDescriptorHandle { Ptr = _rtvHeap.GetCPUDescriptorHandleForHeapStart().Ptr + (nuint)((slot * MaxDepth + (uint)depth) * _rtvInc) };
		_ctx.Device.CreateRenderTargetView(resource, (RenderTargetViewDesc*)null, rtv);

		t.Resource = resource;
		t.Rtv = rtv;
		t.State = ResourceStates.RenderTarget;
		t.Created = true;
	}

	private void ReleaseTargets()
	{
		foreach (var pool in _pools)
		{
			for (var i = 0; i < pool.Length; i++)
			{
				if (pool[i].Created)
					pool[i].Resource.Dispose();
				pool[i] = default;
			}
		}
	}

	public void Dispose()
	{
		ReleaseTargets();
		_rtvHeap.Dispose();
	}
}
