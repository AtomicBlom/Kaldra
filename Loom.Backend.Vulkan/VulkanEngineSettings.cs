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

	/// <summary>Prefer a discrete GPU when several suitable devices are present (the default); set false to take the first suitable device.</summary>
	public bool PreferDiscreteGpu { get; set; } = true;
}
