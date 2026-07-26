using System.Runtime.CompilerServices;
using Loom.Core.Hosting;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D12;
using Silk.NET.DXGI;

namespace Loom.Backend.DirectX12;

/// <summary>
/// The DirectX 12 <see cref="IRenderBackend"/> — the factory that stands up a D3D12 device and returns a
/// <see cref="DirectX12Engine"/> as a neutral <see cref="IEngine"/>. It self-registers into
/// <see cref="Backends.Available"/> from a module initializer, so simply referencing (loading) this
/// assembly makes <c>"DirectX12"</c> a selectable backend — no explicit wiring in app code. This is the second
/// backend slotting in behind the unchanged describe/execute layers.
/// </summary>
public sealed class DirectX12Backend : IRenderBackend
{
#pragma warning disable CA2255
	/// <summary>
	/// Register the DirectX12 backend as the Loom.DirectX12 assembly loads — but only if
	/// <see cref="IsSupported"/> confirms D3D12 is actually available. On a box (or OS) without it the backend then
	/// never appears in <see cref="Backends.Available"/>, so selection falls through to a working one instead
	/// of crashing later in <see cref="Create"/>.
	/// </summary>
	[ModuleInitializer]
	internal static void Register()
	{
		if (IsSupported())
			Backends.Register(new DirectX12Backend());
	}
#pragma warning restore CA2255

	/// <summary>
	/// Probe — without throwing — whether this machine can run the DirectX12 backend: D3D12 loads and a device can be
	/// created at the engine's minimum feature level (11_0). The probe device is released immediately. Any failure
	/// (no <c>d3d12.dll</c> on a non-Windows OS, no DirectX12-capable adapter) is swallowed and reported as <c>false</c>
	/// rather than faulting assembly load.
	/// </summary>
	private static bool IsSupported()
	{
		try
		{
			var d3d12 = D3D12.GetApi();
			var hr = d3d12.CreateDevice(default(ComPtr<IDXGIAdapter>), D3DFeatureLevel.Level110, out ComPtr<ID3D12Device> device);
			if (hr >= 0)
				device.Dispose(); // a device can be created — release the probe instance
			return hr >= 0;
		}
		catch
		{
			return false; // d3d12.dll absent (non-Windows) or no capable adapter — DirectX12 isn't available here
		}
	}

	public string Name => "DirectX12";

	/// <summary>
	/// Build a D3D12 device bound to <paramref name="surface"/> (the window's Win32 <c>HWND</c>, passed as an
	/// <see cref="nint"/>) and return a live engine. Where the Vulkan backend expects an <c>IVkSurface</c>, DirectX12
	/// expects the native window handle — the seam passes it opaquely so Core names no windowing type
	/// (<see cref="IRenderBackend.Create"/>). <paramref name="settings"/> may be a <see cref="DirectX12EngineSettings"/>
	/// for DirectX12-only knobs or the base <see cref="EngineSettings"/>.
	/// </summary>
	public IEngine Create(object surface, int width, int height, EngineSettings settings)
	{
		ArgumentNullException.ThrowIfNull(surface);
		ArgumentNullException.ThrowIfNull(settings);

		var dx12Settings = settings as DirectX12EngineSettings;
		if (dx12Settings is null)
		{
			dx12Settings = new DirectX12EngineSettings();
			settings.CopyBaseSettingsTo(dx12Settings);
		}

		if (surface is OffscreenSurface offscreen)
			return new DirectX12Engine(offscreen.Width, offscreen.Height, dx12Settings);

		if (surface is not nint hwnd)
			throw new ArgumentException(
				$"The DirectX12 backend needs the window's Win32 HWND (as an nint, or an {nameof(OffscreenSurface)} for windowless creation); got {surface.GetType().Name}.",
				nameof(surface));
		if (hwnd == 0)
			throw new ArgumentException("The DirectX12 backend was given a null HWND.", nameof(surface));

		return new DirectX12Engine(hwnd, width, height, dx12Settings);
	}
}
