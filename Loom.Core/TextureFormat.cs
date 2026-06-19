namespace Loom.Core;

/// <summary>
/// The pixel encoding a <see cref="TextureData"/> carries — what the texels <em>mean</em>, which the backend
/// maps to a sampled image format (Vulkan <c>R8G8B8A8_*</c>, DXGI <c>R8G8B8A8_*</c>). Backend-neutral; the two
/// encodings M11 needs are an sRGB colour map (albedo) and a linear data map (tangent-space normals).
/// </summary>
public enum TextureFormat
{
	/// <summary>RGBA8 in sRGB — colour data (albedo/base colour); the GPU linearises on sample.</summary>
	Rgba8Srgb,

	/// <summary>RGBA8 linear (unorm) — data, not colour (normal maps, masks); sampled verbatim.</summary>
	Rgba8Unorm,
}