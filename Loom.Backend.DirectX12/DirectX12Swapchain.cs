using Silk.NET.Core.Native;
using Silk.NET.Direct3D12;
using Silk.NET.DXGI;

namespace Loom.Backend.DirectX12;

/// <summary>
/// The DirectX12 flip-model swapchain — the counterpart of <c>Swapchain</c> in the Vulkan backend. Owns the
/// <c>IDXGISwapChain1</c>, an RTV descriptor heap, and one render-target view per backbuffer, and recreates
/// them on resize. The current backbuffer index is tracked manually (it advances by one each present under the
/// flip model), which keeps the type at <c>IDXGISwapChain1</c> — <c>GetCurrentBackBufferIndex</c> is an
/// <c>IDXGISwapChain3</c>-only method we don't need.
/// </summary>
internal sealed unsafe class DirectX12Swapchain : IDirectX12PresentTarget
{
	private readonly DirectX12Context _ctx;
	private readonly nint _hwnd;
	private readonly uint _bufferCount;

	/// <summary>The swapchain buffer format — must be a plain UNORM under the flip model (no _SRGB swapchain).</summary>
	public Format Format { get; }

	/// <summary>
	/// The format the backbuffer RTV is created with — the _SRGB variant of <see cref="Format"/>. Writing through
	/// an sRGB RTV makes the hardware gamma-encode the (linear) shader output on present, matching the Vulkan
	/// backend's sRGB swapchain. Flip-model swapchains can't BE _SRGB, but a UNORM buffer can carry an _SRGB RTV
	/// (same cast family). PSOs that render to the backbuffer must bake against this format, not <see cref="Format"/>.
	/// </summary>
	public Format RenderTargetFormat { get; }

	public ComPtr<IDXGISwapChain1> SwapChain;
	private ComPtr<ID3D12DescriptorHeap> _rtvHeap;
	private readonly ComPtr<ID3D12Resource>[] _backBuffers;
	private CpuDescriptorHandle _rtvStart;
	private uint _index;

	public uint Width { get; private set; }
	public uint Height { get; private set; }

	/// <summary>The backbuffer to render into this frame (the flip model's current index).</summary>
	public uint CurrentIndex => _index;

	public DirectX12Swapchain(DirectX12Context ctx, nint hwnd, int width, int height, uint bufferCount)
	{
		_ctx = ctx;
		_hwnd = hwnd;
		_bufferCount = bufferCount;
		_backBuffers = new ComPtr<ID3D12Resource>[bufferCount];
		Format = Format.FormatR8G8B8A8Unorm; // SDR; HDR colour-space negotiation is a later slice
		RenderTargetFormat = Format.FormatR8G8B8A8UnormSrgb; // sRGB-encode the linear shader output on present (matches Vulkan)

		var heapDesc = new DescriptorHeapDesc
		{
			Type = DescriptorHeapType.Rtv,
			NumDescriptors = bufferCount,
			Flags = DescriptorHeapFlags.None,
			NodeMask = 0
		};
		SilkMarshal.ThrowHResult(ctx.Device.CreateDescriptorHeap(&heapDesc, out _rtvHeap));
		_rtvStart = _rtvHeap.GetCPUDescriptorHandleForHeapStart();

		CreateSwapChain(width, height);
		CreateBackBuffers();
	}

	private void CreateSwapChain(int width, int height)
	{
		Width = (uint)Math.Max(1, width);
		Height = (uint)Math.Max(1, height);
		var desc = new SwapChainDesc1
		{
			Width = Width,
			Height = Height,
			Format = Format,
			Stereo = false,
			SampleDesc = new SampleDesc { Count = 1, Quality = 0 },
			BufferUsage = DXGI.UsageRenderTargetOutput,
			BufferCount = _bufferCount,
			Scaling = Scaling.Stretch,
			SwapEffect = SwapEffect.FlipDiscard,
			AlphaMode = AlphaMode.Unspecified,
			Flags = 0
		};
		SilkMarshal.ThrowHResult(_ctx.Factory.CreateSwapChainForHwnd(
			_ctx.GraphicsQueue, _hwnd, &desc, (SwapChainFullscreenDesc*)null, default(ComPtr<IDXGIOutput>), ref SwapChain));
		_ctx.Factory.MakeWindowAssociation(_hwnd, 1u << 1); // DXGI_MWA_NO_ALT_ENTER — don't let DXGI hijack Alt+Enter
		_index = 0;
	}

	private void CreateBackBuffers()
	{
		// Create an _SRGB RTV over the UNORM backbuffer so the (linear) shader output is gamma-encoded on write —
		// the flip-model way to get sRGB present (the swapchain buffer itself must stay UNORM). Without an explicit
		// desc the RTV would inherit the UNORM format and present linear values un-encoded (too dark/saturated).
		var rtvDesc = new RenderTargetViewDesc { Format = RenderTargetFormat, ViewDimension = RtvDimension.Texture2D };
		for (uint i = 0; i < _bufferCount; i++)
		{
			SilkMarshal.ThrowHResult(SwapChain.GetBuffer(i, out _backBuffers[i]));
			_ctx.Device.CreateRenderTargetView(_backBuffers[i], &rtvDesc, RtvHandle(i));
		}
	}

	/// <summary>The CPU RTV handle for backbuffer <paramref name="i"/> (hand-indexed off the heap start).</summary>
	public CpuDescriptorHandle RtvHandle(uint i) => new() { Ptr = _rtvStart.Ptr + (nuint)(i * _ctx.RtvDescriptorSize) };

	public ComPtr<ID3D12Resource> BackBuffer(uint i) => _backBuffers[i];

	public void Present(bool vsync)
	{
		SilkMarshal.ThrowHResult(SwapChain.Present(vsync ? 1u : 0u, 0u));
		_index = (_index + 1) % _bufferCount;
	}

	/// <summary>Recreate the backbuffers at a new size. The caller must have drained the GPU first.</summary>
	public void Resize(int width, int height)
	{
		for (uint i = 0; i < _bufferCount; i++)
		{
			_backBuffers[i].Dispose();
			_backBuffers[i] = default;
		}
		Width = (uint)Math.Max(1, width);
		Height = (uint)Math.Max(1, height);
		SilkMarshal.ThrowHResult(SwapChain.ResizeBuffers(_bufferCount, Width, Height, Format, 0));
		_index = 0;
		CreateBackBuffers();
	}

	public void Dispose()
	{
		for (uint i = 0; i < _bufferCount; i++)
			_backBuffers[i].Dispose();
		_rtvHeap.Dispose();
		SwapChain.Dispose();
	}
}
