using Loom.Core;

using Silk.NET.Core.Native;
using Silk.NET.Direct3D12;

using DxgiFormat = Silk.NET.DXGI.Format;

namespace Loom.DX12;

/// <summary>
/// The transient render targets a graph declares — the DX12 counterpart of the Vulkan backend's
/// <c>TransientResources</c>. Each colour transient gets one texture (plus an RTV) per frame-in-flight slot,
/// and each depth transient one texture (plus a DSV) per slot, so a frame's write never races a prior frame's
/// read. The backbuffer is <em>not</em> here (it's the swapchain's facade, handled by the engine).
/// <para>
/// This step allocates colour + depth targets with no aliasing (one allocation each, like the Vulkan backend's
/// M3); lifetime-driven aliasing (the <c>AliasPlan</c> buckets) and buffers follow.
/// </para>
/// </summary>
internal sealed unsafe class DX12Transients : IDisposable
{
	public struct Target
	{
		public ComPtr<ID3D12Resource> Resource;
		public CpuDescriptorHandle View; // RTV for a colour target, DSV for a depth target
		public DxgiFormat Format;
		public ResourceStates State;
		public bool IsDepth;
	}

	private readonly Target[][] _targets; // [slot][resourceId]
	private readonly (uint W, uint H)[] _extents; // by resourceId (same across slots)
	private readonly bool[] _isTransient; // by resourceId
	private ComPtr<ID3D12DescriptorHeap> _rtvHeap;
	private ComPtr<ID3D12DescriptorHeap> _dsvHeap;

	public DX12Transients(DX12Context ctx, RenderGraph graph, uint bufferCount, uint swapWidth, uint swapHeight)
	{
		var count = graph.Resources.Length;
		_isTransient = new bool[count];
		_extents = new (uint, uint)[count];

		var colorTargets = new List<ResourceHandle>();
		var depthTargets = new List<ResourceHandle>();
		foreach (var resource in graph.Resources)
		{
			if (resource.Kind == ResourceKind.Backbuffer)
				continue; // the swapchain facade, owned by the engine
			if (resource.Kind is not (ResourceKind.Color or ResourceKind.Depth))
				throw new NotSupportedException($"DX12: resource kind {resource.Kind} ('{resource.Name}') lands in a later executor slice (buffer/persistent).");
			_isTransient[resource.Id] = true;
			_extents[resource.Id] = Size(resource, swapWidth, swapHeight);
			(resource.Kind == ResourceKind.Depth ? depthTargets : colorTargets).Add(resource);
		}

		_targets = new Target[bufferCount][];
		for (uint slot = 0; slot < bufferCount; slot++)
			_targets[slot] = new Target[count];

		AllocateColor(ctx, colorTargets, bufferCount);
		AllocateDepth(ctx, depthTargets, bufferCount);
	}

	private void AllocateColor(DX12Context ctx, List<ResourceHandle> colorTargets, uint bufferCount)
	{
		var total = (uint)(bufferCount * colorTargets.Count);
		if (total == 0)
			return;

		var increment = ctx.Device.GetDescriptorHandleIncrementSize(DescriptorHeapType.Rtv);
		var heapDesc = new DescriptorHeapDesc { Type = DescriptorHeapType.Rtv, NumDescriptors = total, Flags = DescriptorHeapFlags.None, NodeMask = 0 };
		SilkMarshal.ThrowHResult(ctx.Device.CreateDescriptorHeap(&heapDesc, out _rtvHeap));
		var start = _rtvHeap.GetCPUDescriptorHandleForHeapStart();

		uint slotIndex = 0;
		for (uint slot = 0; slot < bufferCount; slot++)
			foreach (var resource in colorTargets)
			{
				var (w, h) = _extents[resource.Id];
				var format = DX12FormatMapping.ToDxgi(resource.Format);
				var texture = CreateColorTexture(ctx, w, h, format);
				var rtv = new CpuDescriptorHandle { Ptr = start.Ptr + (nuint)(slotIndex++ * increment) };
				ctx.Device.CreateRenderTargetView(texture, (RenderTargetViewDesc*)null, rtv);
				_targets[slot][resource.Id] = new Target { Resource = texture, View = rtv, Format = format, State = ResourceStates.Common, IsDepth = false };
			}
	}

	private void AllocateDepth(DX12Context ctx, List<ResourceHandle> depthTargets, uint bufferCount)
	{
		var total = (uint)(bufferCount * depthTargets.Count);
		if (total == 0)
			return;

		var increment = ctx.Device.GetDescriptorHandleIncrementSize(DescriptorHeapType.Dsv);
		var heapDesc = new DescriptorHeapDesc { Type = DescriptorHeapType.Dsv, NumDescriptors = total, Flags = DescriptorHeapFlags.None, NodeMask = 0 };
		SilkMarshal.ThrowHResult(ctx.Device.CreateDescriptorHeap(&heapDesc, out _dsvHeap));
		var start = _dsvHeap.GetCPUDescriptorHandleForHeapStart();

		uint slotIndex = 0;
		for (uint slot = 0; slot < bufferCount; slot++)
			foreach (var resource in depthTargets)
			{
				var (w, h) = _extents[resource.Id];
				var format = DX12FormatMapping.ToDxgi(resource.Format);
				var texture = CreateDepthTexture(ctx, w, h, format);
				var dsv = new CpuDescriptorHandle { Ptr = start.Ptr + (nuint)(slotIndex++ * increment) };
				ctx.Device.CreateDepthStencilView(texture, (DepthStencilViewDesc*)null, dsv);
				_targets[slot][resource.Id] = new Target { Resource = texture, View = dsv, Format = format, State = ResourceStates.Common, IsDepth = true };
			}
	}

	public bool IsTransient(int id) => _isTransient[id];
	public bool IsDepth(uint slot, int id) => _targets[slot][id].IsDepth;
	public (uint W, uint H) Extent(int id) => _extents[id];
	public DxgiFormat Format(uint slot, int id) => _targets[slot][id].Format;
	public CpuDescriptorHandle View(uint slot, int id) => _targets[slot][id].View;
	public ComPtr<ID3D12Resource> Resource(uint slot, int id) => _targets[slot][id].Resource;
	public ResourceStates State(uint slot, int id) => _targets[slot][id].State;
	public void SetState(uint slot, int id, ResourceStates state) => _targets[slot][id].State = state;

	private static (uint W, uint H) Size(ResourceHandle resource, uint swapWidth, uint swapHeight)
	{
		var domain = resource.Domain;
		if (domain.IsBackbuffer)
			return (Scale(swapWidth, resource.Scale), Scale(swapHeight, resource.Scale));
		if (domain.RelativeScale > 0f)
			return (Scale(swapWidth, domain.RelativeScale * resource.Scale), Scale(swapHeight, domain.RelativeScale * resource.Scale));
		var (w, h) = domain.Resolve(resource.Scale);
		return ((uint)Math.Max(1, w), (uint)Math.Max(1, h));
	}

	private static uint Scale(uint value, float scale) => (uint)Math.Max(1, (int)MathF.Round(value * scale));

	private static ComPtr<ID3D12Resource> CreateColorTexture(DX12Context ctx, uint width, uint height, DxgiFormat format)
	{
		// Optimized clear value matching the executor's clear, so the debug layer doesn't warn on ClearRenderTargetView.
		var clear = new ClearValue { Format = format };
		clear.Anonymous.Color[3] = 1f;
		return CreateTexture(ctx, width, height, format, ResourceFlags.AllowRenderTarget, ResourceStates.Common, &clear);
	}

	private static ComPtr<ID3D12Resource> CreateDepthTexture(DX12Context ctx, uint width, uint height, DxgiFormat format)
	{
		// Optimized clear value matching the executor's depth clear (1.0), so ClearDepthStencilView is warning-free.
		var clear = new ClearValue { Format = format };
		clear.Anonymous.DepthStencil = new DepthStencilValue { Depth = 1f, Stencil = 0 };
		return CreateTexture(ctx, width, height, format, ResourceFlags.AllowDepthStencil, ResourceStates.Common, &clear);
	}

	private static ComPtr<ID3D12Resource> CreateTexture(DX12Context ctx, uint width, uint height, DxgiFormat format, ResourceFlags flags, ResourceStates initialState, ClearValue* clear)
	{
		var heapProps = new HeapProperties { Type = HeapType.Default, CPUPageProperty = CpuPageProperty.Unknown, MemoryPoolPreference = MemoryPool.Unknown, CreationNodeMask = 1, VisibleNodeMask = 1 };
		var desc = new ResourceDesc
		{
			Dimension = ResourceDimension.Texture2D,
			Alignment = 0,
			Width = width,
			Height = height,
			DepthOrArraySize = 1,
			MipLevels = 1,
			Format = format,
			SampleDesc = new Silk.NET.DXGI.SampleDesc { Count = 1, Quality = 0 },
			Layout = TextureLayout.LayoutUnknown,
			Flags = flags,
		};
		ComPtr<ID3D12Resource> texture = default;
		SilkMarshal.ThrowHResult(ctx.Device.CreateCommittedResource(
			&heapProps, HeapFlags.None, &desc, initialState, clear, out texture));
		return texture;
	}

	public void Dispose()
	{
		foreach (var slot in _targets)
			foreach (var target in slot)
				if (target.Resource.Handle is not null)
					target.Resource.Dispose();
		if (_rtvHeap.Handle is not null)
			_rtvHeap.Dispose();
		if (_dsvHeap.Handle is not null)
			_dsvHeap.Dispose();
	}
}
