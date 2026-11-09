using Silk.NET.Core.Native;
using Silk.NET.Direct3D12;
using Silk.NET.DXGI;

namespace Loom.Backend.DirectX12;

/// <summary>
/// The render targets the engine draws into and hands off each frame, plus how they're resized/disposed — shared
/// by <see cref="DirectX12Swapchain"/> (a live window's flip-model swapchain) and
/// <see cref="DirectX12OffscreenTarget"/> (owned committed resources with no window/swapchain, for headless/CI
/// rendering via <see cref="Loom.Core.Hosting.Surface.Offscreen"/>). <see cref="DirectX12Engine"/>'s per-frame
/// recording loop is written once against this surface; only handing the finished frame off (a real
/// <c>IDXGISwapChain1.Present</c> vs. an offscreen no-op) differs between the two.
/// </summary>
internal unsafe interface IDirectX12PresentTarget : IDisposable
{
	/// <summary>The render targets' buffer format.</summary>
	Format Format { get; }

	/// <summary>The format the backbuffer RTV is created with (the sRGB-encoding view over <see cref="Format"/>).</summary>
	Format RenderTargetFormat { get; }

	/// <summary>True when the target presents HDR (scRGB: half floats, linear, 1.0 = 80 nits) — asked for with
	/// <c>EngineSettings.HdrOutput</c> and granted by the display. What a frame's <c>IRenderContext.HdrOutput</c> reports.</summary>
	bool HdrActive { get; }

	/// <summary>The render targets' pixel width.</summary>
	uint Width { get; }

	/// <summary>The render targets' pixel height.</summary>
	uint Height { get; }

	/// <summary>The buffer to render into this frame.</summary>
	uint CurrentIndex { get; }

	/// <summary>The committed resource backing buffer <paramref name="i"/>.</summary>
	ComPtr<ID3D12Resource> BackBuffer(uint i);

	/// <summary>The CPU RTV handle for buffer <paramref name="i"/>.</summary>
	CpuDescriptorHandle RtvHandle(uint i);

	/// <summary>Hand the rendered buffer off — present it to the window (advancing <see cref="CurrentIndex"/>), or
	/// just advance the round-robin index for an offscreen target with no presentation engine.</summary>
	void Present(bool vsync);

	/// <summary>Recreate the buffers at a new size. The caller must have drained the GPU first.</summary>
	void Resize(int width, int height);
}
