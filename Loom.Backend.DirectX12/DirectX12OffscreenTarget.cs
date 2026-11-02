using Silk.NET.Core.Native;
using Silk.NET.Direct3D12;
using Silk.NET.DXGI;

namespace Loom.Backend.DirectX12;

/// <summary>
/// An owned set of committed render-target resources the engine draws into with no window or swapchain — the
/// present target for <see cref="Loom.Core.Hosting.Surface.Offscreen"/> engines (CI screenshot regression,
/// off-screen tooling, software-rasterizer runs). Sized to the windowed swapchain's usual buffer count so the same
/// per-frame-slot bookkeeping applies. Mirrors <see cref="DirectX12Swapchain"/>'s UNORM-buffer/sRGB-RTV split (a
/// plain UNORM resource with an _SRGB render-target view, so the linear shader output is gamma-encoded on write)
/// even though nothing here needs the flip-model's ban on an _SRGB buffer format — it keeps both present targets
/// pixel-identical.
/// </summary>
internal sealed unsafe class DirectX12OffscreenTarget : IDirectX12PresentTarget
{
	/// <summary>Never: an offscreen target is read back as 8-bit images.</summary>
	public bool HdrActive => false;

	private readonly DirectX12Context _ctx;
	private readonly uint _bufferCount;
	private readonly ComPtr<ID3D12Resource>[] _buffers;
	private ComPtr<ID3D12DescriptorHeap> _rtvHeap;
	private CpuDescriptorHandle _rtvStart;
	private uint _index;

	public Format Format { get; } = Format.FormatR8G8B8A8Unorm;
	public Format RenderTargetFormat { get; } = Format.FormatR8G8B8A8UnormSrgb;
	public uint Width { get; private set; }
	public uint Height { get; private set; }
	public uint CurrentIndex => _index;

	public DirectX12OffscreenTarget(DirectX12Context ctx, int width, int height, uint bufferCount)
	{
		_ctx = ctx;
		_bufferCount = bufferCount;
		_buffers = new ComPtr<ID3D12Resource>[bufferCount];

		var heapDesc = new DescriptorHeapDesc
		{
			Type = DescriptorHeapType.Rtv,
			NumDescriptors = bufferCount,
			Flags = DescriptorHeapFlags.None,
			NodeMask = 0
		};
		SilkMarshal.ThrowHResult(ctx.Device.CreateDescriptorHeap(&heapDesc, out _rtvHeap));
		_rtvStart = _rtvHeap.GetCPUDescriptorHandleForHeapStart();

		Create(width, height);
	}

	private void Create(int width, int height)
	{
		Width = (uint)Math.Max(1, width);
		Height = (uint)Math.Max(1, height);

		var heapProps = new HeapProperties
		{
			Type = HeapType.Default,
			CPUPageProperty = CpuPageProperty.Unknown,
			MemoryPoolPreference = MemoryPool.Unknown,
			CreationNodeMask = 1,
			VisibleNodeMask = 1
		};
		var desc = new ResourceDesc
		{
			Dimension = ResourceDimension.Texture2D,
			Alignment = 0,
			Width = Width,
			Height = Height,
			DepthOrArraySize = 1,
			MipLevels = 1,
			Format = Format,
			SampleDesc = new SampleDesc { Count = 1, Quality = 0 },
			Layout = TextureLayout.LayoutUnknown,
			Flags = ResourceFlags.AllowRenderTarget,
		};
		var clear = new ClearValue { Format = RenderTargetFormat };
		var rtvDesc = new RenderTargetViewDesc { Format = RenderTargetFormat, ViewDimension = RtvDimension.Texture2D };

		for (uint i = 0; i < _bufferCount; i++)
		{
			// D3D12_RESOURCE_STATE_COMMON == D3D12_RESOURCE_STATE_PRESENT (both 0) — the same "fresh buffer starts
			// presentable" state DirectX12Engine assumes for every backbuffer index, windowed or offscreen.
			SilkMarshal.ThrowHResult(_ctx.Device.CreateCommittedResource(
				&heapProps, HeapFlags.None, &desc, ResourceStates.Common, &clear, out _buffers[i]));
			_ctx.Device.CreateRenderTargetView(_buffers[i], &rtvDesc, RtvHandle(i));
		}
	}

	public CpuDescriptorHandle RtvHandle(uint i) => new() { Ptr = _rtvStart.Ptr + (nuint)(i * _ctx.RtvDescriptorSize) };

	public ComPtr<ID3D12Resource> BackBuffer(uint i) => _buffers[i];

	// No presentation engine to hand off to — just advance the round-robin index.
	public void Present(bool vsync) => _index = (_index + 1) % _bufferCount;

	public void Resize(int width, int height)
	{
		for (uint i = 0; i < _bufferCount; i++)
		{
			_buffers[i].Dispose();
			_buffers[i] = default;
		}
		_index = 0;
		Create(width, height);
	}

	public void Dispose()
	{
		for (uint i = 0; i < _bufferCount; i++)
			if (_buffers[i].Handle is not null)
				_buffers[i].Dispose();
		_rtvHeap.Dispose();
	}
}
