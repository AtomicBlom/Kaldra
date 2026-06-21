namespace Loom.Core.Hosting;

/// <summary>
/// A rendering backend — the factory for an <see cref="IEngine"/> on a particular graphics API (Vulkan today,
/// a future DirectX12). Backends self-register into <see cref="Engine.AvailableEngines"/>; the host picks one by
/// <see cref="Name"/> at runtime and calls <see cref="Create"/>. This is the seam that lets a second backend
/// slot in without touching the describe/execute layers (Planning.md §2 portability invariant).
/// </summary>
public interface IRenderBackend
{
	/// <summary>The backend's stable selection name (e.g. <c>"Vulkan"</c>, <c>"DirectX12"</c>) — matched against <see cref="EngineSettings.PreferredBackend"/>.</summary>
	string Name { get; }

	/// <summary>
	/// Create a live engine bound to <paramref name="surface"/>, sized to <paramref name="width"/>×
	/// <paramref name="height"/>, configured by <paramref name="settings"/>. <paramref name="surface"/> is the
	/// host's native surface for this backend, passed opaquely so Core names no windowing/graphics-API type — the
	/// Vulkan backend expects the window's <c>IVkSurface</c>; a DirectX12 backend would expect its native window
	/// handle. <paramref name="settings"/> may be the base <see cref="EngineSettings"/> or this backend's
	/// subclass (which it down-casts to read backend-specific knobs).
	/// </summary>
	IEngine Create(object surface, int width, int height, EngineSettings settings);
}
