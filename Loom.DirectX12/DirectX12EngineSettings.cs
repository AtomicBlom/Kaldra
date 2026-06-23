using Loom.Core.Hosting;

namespace Loom.DirectX12;

/// <summary>
/// DirectX 12-specific engine configuration — the <see cref="EngineSettings"/> subclass a host populates when
/// it wants DirectX12-only knobs (the counterpart to <c>VulkanEngineSettings</c>). It is the one sanctioned place
/// the DirectX12 backend type appears in app code; constructing it also loads the Loom.DirectX12 assembly, which
/// self-registers the DirectX12 backend into <see cref="Engine.AvailableEngines"/> (see
/// <see cref="DirectX12Backend"/>). A host that needs no DirectX12-only knob can use the base <see cref="EngineSettings"/>
/// instead, provided it touches some Loom.DirectX12 type first.
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
	/// technique's HLSL (<c>&lt;name&gt;.hlsl</c>) + reflection (<c>&lt;name&gt;.json</c>) here at
	/// <see cref="IEngine.DefineTechnique"/>, the counterpart of <c>VulkanEngineSettings.ShaderDirectory</c>.
	/// A shader with no resolved HLSL falls back to the backend's built-in stand-in.
	/// </summary>
	public string ShaderDirectory { get; set; } = "shaders";
}
