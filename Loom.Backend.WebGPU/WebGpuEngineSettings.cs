using Loom.Core.Hosting;

namespace Loom.Backend.WebGPU;

/// <summary>
/// WebGPU-only engine knobs, extending the neutral <see cref="EngineSettings"/>. Constructing this type is what
/// loads the backend assembly and fires its self-registration, mirroring the other backends' settings pattern.
/// </summary>
public sealed class WebGpuEngineSettings : EngineSettings
{
	public WebGpuEngineSettings() => PreferredBackend = "WebGPU";

	/// <summary>
	/// Resolves a shader artifact (a <c>.wgsl</c> module or its <c>.wgsl.json</c> reflection sidecar) by file
	/// name, or null when the artifact does not exist. Defaults to the engine's embedded shaders plus the host
	/// app's entry-assembly embedded shaders — a browser host has no shader directory to fall back to.
	/// </summary>
	public Func<string, byte[]?>? ShaderResolver { get; set; }
}
