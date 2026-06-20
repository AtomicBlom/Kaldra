using Loom.Core.Graph;

namespace Loom.Core.Materials;

/// <summary>
/// The pixel encoding of a loaded / sampled source image — what a <see cref="TextureData"/>'s texels
/// <em>mean</em>, which the backend maps to a sampled image format (Vulkan/DXGI <c>R8G8B8A8_*</c>). Backend-neutral.
/// <para>
/// Deliberately distinct from <see cref="Format"/>: this is the <b>source-texture / sampled-encoding</b>
/// vocabulary (it will grow toward block-compressed formats — BC/ASTC/ETC2), whereas <see cref="Format"/> is the
/// <b>render-graph attachment / target</b> vocabulary (HDR, packed, depth). They overlap on the plain RGBA8
/// encodings on purpose and diverge as each domain's needs grow, so keep them separate rather than unifying.
/// </para>
/// </summary>
public enum TextureFormat
{
	/// <summary>RGBA8 in sRGB — colour data (albedo/base colour); the GPU linearises on sample.</summary>
	Rgba8Srgb,

	/// <summary>RGBA8 linear (unorm) — data, not colour (normal maps, masks); sampled verbatim.</summary>
	Rgba8Unorm,

	/// <summary>RGBA 16-bit float — HDR source imagery (an image-based-lighting environment / equirectangular map),
	/// where 8-bit unorm would clip the values above 1 that IBL integrates over.</summary>
	Rgba16F,
}
