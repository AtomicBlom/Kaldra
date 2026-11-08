using System.Runtime.InteropServices;
using Loom.Core.Diagnostics;
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

	/// <summary>The swapchain buffer format — a plain UNORM under the flip model (no _SRGB swapchain), or half floats
	/// for HDR.</summary>
	public Format Format { get; private set; }

	/// <summary>
	/// The format the backbuffer RTV is created with — the _SRGB variant of <see cref="Format"/>. Writing through
	/// an sRGB RTV makes the hardware gamma-encode the (linear) shader output on present, matching the Vulkan
	/// backend's sRGB swapchain. Flip-model swapchains can't BE _SRGB, but a UNORM buffer can carry an _SRGB RTV
	/// (same cast family). PSOs that render to the backbuffer must bake against this format, not <see cref="Format"/>.
	/// </summary>
	public Format RenderTargetFormat { get; private set; }

	/// <summary>
	/// True when the swapchain presents scRGB: R16G16B16A16_FLOAT in the linear Rec.709 colour space (G10, 1.0 = 80
	/// nits). Asked for with <c>hdr</c>, and granted only when the display the window is on is in HDR and the swapchain can
	/// present in that colour space; otherwise the swapchain is SDR, this is false, and a warning says why. DXGI would
	/// compose scRGB for an SDR display too, clipping everything past paper white, so that is never granted.
	/// </summary>
	public bool HdrActive { get; private set; }

	public ComPtr<IDXGISwapChain1> SwapChain;
	private ComPtr<ID3D12DescriptorHeap> _rtvHeap;
	private readonly ComPtr<ID3D12Resource>[] _backBuffers;
	private CpuDescriptorHandle _rtvStart;
	private uint _index;

	public uint Width { get; private set; }
	public uint Height { get; private set; }

	/// <summary>The backbuffer to render into this frame (the flip model's current index).</summary>
	public uint CurrentIndex => _index;

	public DirectX12Swapchain(DirectX12Context ctx, nint hwnd, int width, int height, uint bufferCount, bool hdr = false)
	{
		_ctx = ctx;
		_hwnd = hwnd;
		_bufferCount = bufferCount;
		_backBuffers = new ComPtr<ID3D12Resource>[bufferCount];
		UseSdr();

		var heapDesc = new DescriptorHeapDesc
		{
			Type = DescriptorHeapType.Rtv,
			NumDescriptors = bufferCount,
			Flags = DescriptorHeapFlags.None,
			NodeMask = 0
		};
		SilkMarshal.ThrowHResult(ctx.Device.CreateDescriptorHeap(&heapDesc, out ComPtr<ID3D12DescriptorHeap> madeRtvHeap));
		_rtvHeap = madeRtvHeap;
		_rtvStart = _rtvHeap.GetCPUDescriptorHandleForHeapStart();

		CreateSwapChain(width, height);
		if (hdr)
			TryHdr();
		LoomLog.Info($"DirectX12 swapchain format={Format}, hdr={HdrActive}");
		CreateBackBuffers();
	}

	private void UseSdr()
	{
		Format = Format.FormatR8G8B8A8Unorm;
		RenderTargetFormat = Format.FormatR8G8B8A8UnormSrgb; // sRGB-encode the linear shader output on present (matches Vulkan)
		HdrActive = false;
	}

	// Turn the swapchain over to scRGB: half-float buffers presented in the linear Rec.709 colour space. The RTV writes
	// the shader's output as it is, linear, with no encode. False, leaving it SDR and saying why, where the window's
	// display is not in HDR or DXGI cannot present that space.
	private bool TryHdr()
	{
		if (!WindowOnHdrOutput())
		{
			LoomLog.Warning("DirectX12: HDR output was asked for, but the window's display is not in HDR; presenting SDR.");
			return false;
		}
		const Format hdrFormat = Format.FormatR16G16B16A16Float;
		const ColorSpaceType scRgb = ColorSpaceType.RgbFullG10NoneP709;
		using var swapChain3 = SwapChain.QueryInterface<IDXGISwapChain3>();
		SilkMarshal.ThrowHResult(SwapChain.ResizeBuffers(_bufferCount, Width, Height, hdrFormat, 0));
		uint support = 0;
		SilkMarshal.ThrowHResult(swapChain3.CheckColorSpaceSupport(scRgb, ref support));
		if ((support & (uint)SwapChainColorSpaceSupportFlag.Present) == 0)
		{
			SilkMarshal.ThrowHResult(SwapChain.ResizeBuffers(_bufferCount, Width, Height, Format, 0));
			LoomLog.Warning("DirectX12: HDR output was asked for, but the swapchain cannot present scRGB; presenting SDR.");
			return false;
		}
		SilkMarshal.ThrowHResult(swapChain3.SetColorSpace1(scRgb));
		Format = RenderTargetFormat = hdrFormat;
		HdrActive = true;
		return true;
	}

	// Whether the display the window is on is in HDR (Windows' "Use HDR" switched on): its output's colour space is
	// HDR10's (G2084, P2020). DXGI composes an scRGB swapchain on an SDR display too, clipping past paper white, so a
	// swapchain able to present scRGB says nothing about the display. The window's output is the one whose desktop
	// rectangle overlaps the window's most, asked through a factory made now so its outputs are current.
	private bool WindowOnHdrOutput()
	{
		WindowRect window;
		if (GetWindowRect(_hwnd, &window) == 0)
			return false;
		if (_ctx.DXGI.CreateDXGIFactory1(out ComPtr<IDXGIFactory1> factory) < 0)
			return false;
		using (factory)
		{
			var mostOverlap = 0L;
			var hdr = false;
			ComPtr<IDXGIAdapter1> adapter = default;
			for (uint a = 0; factory.EnumAdapters1(a, ref adapter) >= 0; a++)
			{
				using (adapter)
				{
					ComPtr<IDXGIOutput> output = default;
					for (uint o = 0; adapter.EnumOutputs(o, ref output) >= 0; o++)
					{
						using (output)
						{
							if (output.QueryInterface(out ComPtr<IDXGIOutput6> output6) < 0)
								continue;
							using (output6)
							{
								OutputDesc1 desc = default;
								if (output6.GetDesc1(ref desc) < 0)
									continue;
								var bounds = desc.DesktopCoordinates;
								long across = Math.Min(window.Right, bounds.Max.X) - Math.Max(window.Left, bounds.Min.X);
								long down = Math.Min(window.Bottom, bounds.Max.Y) - Math.Max(window.Top, bounds.Min.Y);
								if (across <= 0 || down <= 0 || across * down <= mostOverlap)
									continue;
								mostOverlap = across * down;
								hdr = desc.ColorSpace == ColorSpaceType.RgbFullG2084NoneP2020;
							}
						}
					}
				}
			}
			return hdr;
		}
	}

	private struct WindowRect
	{
		public int Left;
		public int Top;
		public int Right;
		public int Bottom;
	}

	[DllImport("user32.dll")]
	private static extern int GetWindowRect(nint hwnd, WindowRect* rect);

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
		ComPtr<IDXGISwapChain1> madeSwapChain = default;
		SilkMarshal.ThrowHResult(_ctx.Factory.CreateSwapChainForHwnd(
			_ctx.GraphicsQueue, _hwnd, &desc, (SwapChainFullscreenDesc*)null, default(ComPtr<IDXGIOutput>), ref madeSwapChain));
		SwapChain = madeSwapChain;
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
			SilkMarshal.ThrowHResult(SwapChain.GetBuffer(i, out ComPtr<ID3D12Resource> madeBackBuffer));
			_backBuffers[i] = madeBackBuffer;
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
