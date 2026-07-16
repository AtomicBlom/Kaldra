namespace Loom.Core.Hosting;

/// <summary>
/// Backend-neutral engine configuration — the canonical config object a host populates and hands to
/// <see cref="IRenderBackend.Create"/>, in lieu of scattered constructor args and environment variables.
/// A host <em>may</em> read environment variables into it, but the object
/// is the source of truth. Subclass it per backend (<c>VulkanEngineSettings</c>, a future
/// <c>DirectX12EngineSettings</c>) to carry backend-only knobs (preferred adapter, required extensions); the neutral
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
	/// Frame-rate cap for the host's render loop (0 = unlimited). Only meaningful with <see cref="VSync"/> off.
	/// The host owns this: it reads the value and applies the cap to its own loop (e.g. <c>DesktopAppHost</c> sets
	/// it on the window's frame-rate limiter before the first frame); the engine itself never paces against it.
	/// It lives here rather than as a separate host-only option so backend/presentation/frame-pacing configuration
	/// stays in one object.
	/// </summary>
	public double MaxFramesPerSecond { get; set; }

	/// <summary>
	/// Request true HDR display output: negotiate an HDR-capable swapchain colour space and apply the HDR output
	/// transform on the terminal pass when the display supports it, falling back to SDR otherwise.
	/// </summary>
	public bool HdrOutput { get; set; }

	/// <summary>
	/// The backend to select from <see cref="Engine.AvailableEngines"/> by <see cref="IRenderBackend.Name"/>.
	/// Null (the default) means "the first available backend." A host's selection idiom:
	/// <c>Engine.AvailableEngines.FirstOrDefault(e =&gt; e.Name == settings.PreferredBackend) ?? Engine.AvailableEngines.First()</c>.
	/// </summary>
	public string? PreferredBackend { get; set; }

	/// <summary>
	/// Watch the loose shader directory for rebuilt artifacts and hot-reload the affected pipelines. Dev-only;
	/// production apps leave this false (off by default). The engine does NOT compile shaders — an external process
	/// (the MSBuild Slang compile, a <c>dotnet watch</c>, …) rewrites the <c>.spv</c>/<c>.json</c>/<c>.dxil</c>; the
	/// engine just picks them up, invalidating the technique cache and rebuilding on the next frame. Only loose
	/// (on-disk) shaders are watchable — embedded engine shaders can't change at runtime.
	/// </summary>
	public bool WatchShaderDirectory { get; set; }

	/// <summary>
	/// Request a CPU/software rendering adapter (DX12 WARP; a Vulkan CPU device type) instead of a real GPU —
	/// for GPU-less CI runners or deterministic software rendering. Falls back to the best available adapter
	/// when no software one exists, rather than failing engine creation.
	/// </summary>
	public bool PreferSoftwareAdapter { get; set; }

	/// <summary>
	/// Copy every base setting onto <paramref name="target"/> — used by a backend when the host passed the neutral
	/// base type rather than the backend's own settings subclass, so the backend builds its subclass and inherits
	/// all the base knobs. Centralised so a newly-added base setting can't be silently dropped by a backend's
	/// hand-written field-by-field copy.
	/// </summary>
	public void CopyBaseSettingsTo(EngineSettings target)
	{
		ArgumentNullException.ThrowIfNull(target);
		target.EnableValidation = EnableValidation;
		target.VSync = VSync;
		target.MaxFramesPerSecond = MaxFramesPerSecond;
		target.HdrOutput = HdrOutput;
		target.PreferredBackend = PreferredBackend;
		target.WatchShaderDirectory = WatchShaderDirectory;
		target.PreferSoftwareAdapter = PreferSoftwareAdapter;
	}
}
