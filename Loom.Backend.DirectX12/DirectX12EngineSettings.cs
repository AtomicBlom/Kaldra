using Loom.Core.Hosting;

namespace Loom.Backend.DirectX12;

/// <summary>
/// DirectX 12-specific engine configuration — the <see cref="EngineSettings"/> subclass a host populates when
/// it wants DirectX12-only knobs (the counterpart to <c>VulkanEngineSettings</c>). It is the one sanctioned place
/// the DirectX12 backend type appears in app code; constructing it also loads the Loom.Backend.DirectX12 assembly, which
/// self-registers the DirectX12 backend into <see cref="Backends.Available"/> (see
/// <see cref="DirectX12Backend"/>). A host that needs no DirectX12-only knob can use the base <see cref="EngineSettings"/>
/// instead, provided it touches some Loom.Backend.DirectX12 type first.
/// </summary>
public sealed class DirectX12EngineSettings : EngineSettings
{
	/// <summary>
	/// Prefer a discrete / high-performance adapter when several are present (the default). On a single-GPU
	/// machine (e.g. an integrated Adreno on Windows-on-ARM) this is moot — the one suitable adapter is taken.
	/// </summary>
	public bool PreferDiscreteGpu { get; set; } = true;

	/// <summary>
	/// Request the D3D12 GPU-based validation layer in addition to the basic debug layer when
	/// <see cref="EngineSettings.EnableValidation"/> is set. Far more thorough — it patches shaders to validate
	/// resource access, descriptors, and aliasing on the GPU — at a real runtime cost. Defaults on in Debug builds
	/// (strict validation while iterating) and off in Release builds.
	/// </summary>
	public bool GpuBasedValidation { get; set; } =
#if DEBUG
		true;
#else
		false;
#endif

	/// <summary>
	/// Directory of loose shader assets (the Loom SDK's <c>shaders\</c> output): the DirectX12 backend resolves a
	/// technique's signed DXIL (<c>&lt;name&gt;.[vs,ps,cs].dxil</c>) + reflection (<c>&lt;name&gt;.json</c>) here at
	/// <see cref="IEngine.DefineTechnique"/>, the counterpart of <c>VulkanEngineSettings.ShaderDirectory</c>.
	/// Defaults to a <c>shaders</c> folder next to the executable (where the Loom SDK deposits the compiled DXIL) —
	/// the same absolute default as Vulkan, so a host that leaves it unset resolves app shaders on both backends.
	/// A shader whose DXIL or reflection sidecar cannot be resolved throws; there is no substitute shader.
	/// </summary>
	public string ShaderDirectory { get; set; } = Path.Combine(AppContext.BaseDirectory, "shaders");
}
