using Loom.Core.Hosting;
using Loom.Core.Materials;

namespace Loom.Backend.Vulkan;

/// <summary>
/// Vulkan-specific engine configuration — the <see cref="EngineSettings"/> subclass a host populates when it
/// wants to steer Vulkan-only knobs. It is the one sanctioned place a backend type appears in
/// app code; constructing it also loads the Loom.Backend.Vulkan assembly, which self-registers the Vulkan backend into
/// <see cref="Backends.Available"/> (see <see cref="VulkanBackend"/>). A host that needs no Vulkan-only
/// knob can use the base <see cref="EngineSettings"/> instead, provided it touches some Loom.Backend.Vulkan type first.
/// </summary>
public sealed class VulkanEngineSettings : EngineSettings
{
	/// <summary>
	/// Directory the backend resolves logical shader names against — <see cref="TechniqueDescriptor.Shader"/>
	/// <c>"fullscreen"</c> becomes <c>&lt;ShaderDirectory&gt;/fullscreen.spv</c>. Defaults to a <c>shaders</c>
	/// folder next to the executable (where Loom.Shaders deposits the compiled SPIR-V).
	/// </summary>
	public string ShaderDirectory { get; set; } = Path.Combine(AppContext.BaseDirectory, "shaders");

	/// <summary>
	/// Resolves a shader artifact the backend found neither embedded in itself nor under <see cref="ShaderDirectory"/>
	/// (a <c>.spv</c> module or its <c>.json</c> reflection sidecar), by file name such as <c>"ui.spv"</c>; null when the
	/// artifact does not exist. For a host with no shader folder beside the app, as on Android, where an APK carries its
	/// shaders as embedded resources: <see cref="EmbeddedShaders.Resolve"/> finds them in the app's assemblies.
	/// Null (the default) consults nothing past the folder.
	/// </summary>
	public Func<string, byte[]?>? ShaderResolver { get; set; }

	/// <summary>Prefer a discrete GPU when several suitable devices are present (the default); set false to take the first suitable device.</summary>
	public bool PreferDiscreteGpu { get; set; } = true;
}
