using Loom.Core;

using Silk.NET.Core.Native;
using Silk.NET.Direct3D12;

using DxgiFormat = Silk.NET.DXGI.Format;

namespace Loom.DX12;

/// <summary>
/// The transient render targets a graph declares — the DX12 counterpart of the Vulkan backend's
/// <c>TransientResources</c>. Each colour transient gets one texture (plus an RTV) per frame-in-flight slot,
/// so a frame's write never races a prior frame's read. The backbuffer is <em>not</em> here (it's the
/// swapchain's facade, handled by the engine).
/// <para>
/// This step allocates colour targets with no aliasing (one allocation each, like the Vulkan backend's M3);
/// lifetime-driven aliasing (the <c>AliasPlan</c> buckets), depth targets, and buffers follow.
/// </para>
/// </summary>
internal sealed unsafe class DX12Transients : IDisposable
{
	public struct Target
	{
		public ComPtr<ID3D12Resource> Resource;
		public CpuDescriptorHandle Rtv;
		public DxgiFormat Format;
		public ResourceStates State;
	}

	private readonly Target[][] _targets; // [slot][resourceId]
	private readonly (uint W, uint H)[] _extents; // by resourceId (same across slots)
	private readonly bool[] _isTransient; // by resourceId
	private ComPtr<ID3D12DescriptorHeap> _rtvHeap;

	public DX12Transients(DX12Context ctx, RenderGraph graph, uint bufferCount, uint swapWidth, uint swapHeight)
	{
		var count = graph.Resources.Length;
		_isTransient = new bool[count];
		_extents = new (uint, uint)[count];

		var colorTargets = new List<ResourceHandle>();
		foreach (var resource in graph.Resources)
		{
			if (resource.Kind == ResourceKind.Backbuffer)
				continue; // the swapchain facade, owned by the engine
			if (resource.Kind != ResourceKind.Color)
				throw new NotSupportedException($"DX12: resource kind {resource.Kind} ('{resource.Name}') lands in a later executor slice (depth/buffer/persistent).");
			_isTransient[resource.Id] = true;
			_extents[resource.Id] = Size(resource, swapWidth, swapHeight);
			colorTargets.Add(resource);
		}

		var rtvIncrement = ctx.Device.GetDescriptorHandleIncrementSize(DescriptorHeapType.Rtv);
		_targets = new Target[bufferCount][];
		for (uint slot = 0; slot < bufferCount; slot++)
			_targets[slot] = new Target[count];

		var rtvTotal = (uint)(bufferCount * colorTargets.Count);
		if (rtvTotal == 0)
			return;

		var heapDesc = new DescriptorHeapDesc { Type = DescriptorHeapType.Rtv, NumDescriptors = rtvTotal, Flags = DescriptorHeapFlags.None, NodeMask = 0 };
		SilkMarshal.ThrowHResult(ctx.Device.CreateDescriptorHeap(&heapDesc, out _rtvHeap));
		var rtvStart = _rtvHeap.GetCPUDescriptorHandleForHeapStart();

		uint rtvSlot = 0;
		for (uint slot = 0; slot < bufferCount; slot++)
		{
			foreach (var resource in colorTargets)
			{
				var (w, h) = _extents[resource.Id];
				var format = DX12FormatMapping.ToDxgi(resource.Format);
				var texture = CreateColorTexture(ctx, w, h, format);
				var rtv = new CpuDescriptorHandle { Ptr = rtvStart.Ptr + (nuint)(rtvSlot * rtvIncrement) };
				ctx.Device.CreateRenderTargetView(texture, (RenderTargetViewDesc*)null, rtv);
				_targets[slot][resource.Id] = new Target { Resource = texture, Rtv = rtv, Format = format, State = ResourceStates.Common };
				rtvSlot++;
			}
		}
	}

	public bool IsTransient(int id) => _isTransient[id];
	public (uint W, uint H) Extent(int id) => _extents[id];
	public DxgiFormat Format(uint slot, int id) => _targets[slot][id].Format;
	public CpuDescriptorHandle Rtv(uint slot, int id) => _targets[slot][id].Rtv;
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
			Flags = ResourceFlags.AllowRenderTarget
		};
		// Optimized clear value matching the executor's clear, so the debug layer doesn't warn on ClearRenderTargetView.
		var clear = new ClearValue { Format = format };
		clear.Anonymous.Color[3] = 1f;

		ComPtr<ID3D12Resource> texture = default;
		SilkMarshal.ThrowHResult(ctx.Device.CreateCommittedResource(
			&heapProps, HeapFlags.None, &desc, ResourceStates.Common, &clear, out texture));
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
	}
}
