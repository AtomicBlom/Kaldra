using Loom.Core.Graph;

namespace Loom.Core.Materials;

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

	/// <summary>Mirror and repeat — coordinates outside <c>[0,1]</c> are mirrored at each integer boundary,
	/// producing a seamless alternating-direction tile.</summary>
	MirrorRepeat,

	/// <summary>Clamp to border — coordinates outside <c>[0,1]</c> sample a constant border colour (opaque
	/// black by default). Useful for shadow maps where samples outside the light frustum should read zero.</summary>
	ClampToBorder,

	/// <summary>Mirror once then clamp to edge — coordinates below 0 are mirrored into <c>[0,1]</c>;
	/// coordinates above 1 clamp to the edge texel. Mirrors the <c>D3D12_TEXTURE_ADDRESS_MODE_MIRROR_ONCE</c>
	/// and Vulkan <c>VK_SAMPLER_ADDRESS_MODE_MIRROR_CLAMP_TO_EDGE</c> semantics.</summary>
	MirrorClampToEdge,
}
