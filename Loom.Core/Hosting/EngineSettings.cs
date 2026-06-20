namespace Loom.Core.Hosting;

/// <summary>
/// Backend-neutral engine configuration — the canonical config object a host populates and hands to
/// <see cref="IRenderBackend.Create"/>, replacing the scattered constructor args and environment variables of
/// earlier milestones (Planning.md §3). A host <em>may</em> read environment variables into it, but the object
/// is the source of truth. Subclass it per backend (<c>VulkanEngineSettings</c>, a future
/// <c>DX12EngineSettings</c>) to carry backend-only knobs (preferred adapter, required extensions); the neutral
/// engine surface only ever reads the base properties, and a backend down-casts to read its own.
/// </summary>
public class EngineSettings
{
	/// <summary>
	/// Enable the backend's validation/debug layer when available (Vulkan validation layers, the D3D12 debug
	/// layer). On by default for development; a backend degrades gracefully if the layer is absent.
	/// </summary>
	public bool EnableValidation { get; set; } = true;

	/// <summary>
	/// Present with v-sync — a paced, tear-free present (FIFO/Mailbox). When false the backend uses the
	/// lowest-latency present mode it has (Immediate where available, else Mailbox); pair with
	/// <see cref="MaxFramesPerSecond"/> to bound the uncapped loop.
	/// </summary>
	public bool VSync { get; set; } = true;

	/// <summary>
	/// Frame-rate cap for the host's render loop (0 = unlimited). Only meaningful with <see cref="VSync"/> off;
	/// a host-loop concern the engine merely records so the configuration lives in one place.
	/// </summary>
	public double MaxFramesPerSecond { get; set; }

	/// <summary>
	/// Request true HDR display output (Planning.md §3/§9): negotiate an HDR-capable swapchain colour space and
	/// apply the HDR output transform on the terminal pass when the display supports it, falling back to SDR
	/// otherwise. The HDR output path lands across M7-A7/B6; until then this is recorded but inert.
	/// </summary>
	public bool HdrOutput { get; set; }

	/// <summary>
	/// The backend to select from <see cref="Engine.AvailableEngines"/> by <see cref="IRenderBackend.Name"/>.
	/// Null (the default) means "the first available backend." A host's selection idiom:
	/// <c>Engine.AvailableEngines.FirstOrDefault(e =&gt; e.Name == settings.PreferredBackend) ?? Engine.AvailableEngines.First()</c>.
	/// </summary>
	public string? PreferredBackend { get; set; }

	/// <summary>
	/// Watch the loose shader directory for rebuilt artifacts and hot-reload the affected pipelines (M13). Dev-only;
	/// production apps leave this false (off by default). The engine does NOT compile shaders — an external process
	/// (the MSBuild Slang compile, a <c>dotnet watch</c>, …) rewrites the <c>.spv</c>/<c>.json</c>/<c>.dxil</c>; the
	/// engine just picks them up, invalidating the technique cache and rebuilding on the next frame. Only loose
	/// (on-disk) shaders are watchable — embedded engine shaders can't change at runtime.
	/// </summary>
	public bool WatchShaderDirectory { get; set; }
}
