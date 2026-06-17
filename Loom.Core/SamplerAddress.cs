namespace Loom.Core;

/// <summary>
/// How a technique's sampler treats texture coordinates outside <c>[0,1]</c> — technique state the backend
/// bakes into its sampler (Vulkan <c>VkSamplerAddressMode</c>, D3D12 <c>D3D12_TEXTURE_ADDRESS_MODE</c>). Like
/// <see cref="BlendMode"/> and <see cref="TechniqueDescriptor.LinearFilter"/>, it is fixed-function intent
/// reflection can't infer, so the technique states it. The post passes keep the default <see cref="Clamp"/>
/// (a fullscreen sample never leaves the unit square); material techniques that tile a texture across geometry
/// use <see cref="Repeat"/>.
/// </summary>
public enum SamplerAddress
{
	/// <summary>Clamp to edge — coordinates outside <c>[0,1]</c> sample the nearest edge texel. The default.</summary>
	Clamp,

	/// <summary>Repeat (wrap) — the texture tiles, so UVs greater than 1 sample it again from the start.</summary>
	Repeat,
}
