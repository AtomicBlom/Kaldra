using System.Runtime.CompilerServices;

using Loom.Core;

namespace Loom.DX12;

/// <summary>
/// The DirectX 12 <see cref="IRenderBackend"/> — the factory that stands up a D3D12 device and returns a
/// <see cref="DX12Engine"/> as a neutral <see cref="IEngine"/>. It self-registers into
/// <see cref="Engine.AvailableEngines"/> from a module initializer, so simply referencing (loading) this
/// assembly makes <c>"DX12"</c> a selectable backend — no explicit wiring in app code. This is the second
/// backend slotting in behind the unchanged describe/execute layers (Planning.md §2 portability invariant).
/// </summary>
public sealed class DX12Backend : IRenderBackend
{
	/// <summary>Register the DX12 backend as the Loom.DX12 assembly loads (Planning.md §5 backend registry).</summary>
	[ModuleInitializer]
	internal static void Register() => Engine.Register(new DX12Backend());

	public string Name => "DX12";

	/// <summary>
	/// Build a D3D12 device bound to <paramref name="surface"/> (the window's Win32 <c>HWND</c>, passed as an
	/// <see cref="nint"/>) and return a live engine. Where the Vulkan backend expects an <c>IVkSurface</c>, DX12
	/// expects the native window handle — the seam passes it opaquely so Core names no windowing type
	/// (<see cref="IRenderBackend.Create"/>). <paramref name="settings"/> may be a <see cref="DX12EngineSettings"/>
	/// for DX12-only knobs or the base <see cref="EngineSettings"/>.
	/// </summary>
	public IEngine Create(object surface, int width, int height, EngineSettings settings)
	{
		ArgumentNullException.ThrowIfNull(surface);
		ArgumentNullException.ThrowIfNull(settings);

		if (surface is not nint hwnd)
			throw new ArgumentException(
				$"The DX12 backend needs the window's Win32 HWND (as an nint); got {surface.GetType().Name}.",
				nameof(surface));
		if (hwnd == 0)
			throw new ArgumentException("The DX12 backend was given a null HWND.", nameof(surface));

		var dx12Settings = settings as DX12EngineSettings ?? new DX12EngineSettings
		{
			EnableValidation = settings.EnableValidation,
			VSync = settings.VSync,
			MaxFramesPerSecond = settings.MaxFramesPerSecond,
			HdrOutput = settings.HdrOutput,
			PreferredBackend = settings.PreferredBackend
		};

		return new DX12Engine(hwnd, width, height, dx12Settings);
	}
}
