using System.Numerics;
using Loom.Core.Materials;

namespace Loom.Assets;

/// <summary>
/// A glTF metallic-roughness material in neutral form — its decoded textures (each ready for
/// <c>IEngine.CreateTexture</c>) plus the scalar factors that modulate them. A channel's texture is null when the
/// material specifies only a factor (or omits the channel); the factor still applies, so a consumer multiplies the
/// two and uses the factor alone where the texture is absent. The metallic-roughness packing follows the glTF
/// spec — one linear texture with roughness in G and metallic in B — and occlusion (R) frequently shares that
/// same image.
/// </summary>
public sealed class ModelMaterial
{
	/// <summary>Base-colour / albedo texture (sRGB), or null.</summary>
	public TextureData? BaseColor { get; init; }

	/// <summary>Tangent-space normal map (linear), or null.</summary>
	public TextureData? Normal { get; init; }

	/// <summary>Metallic-roughness texture (linear), glTF packing: G = roughness, B = metallic. Null when factor-only.</summary>
	public TextureData? MetallicRoughness { get; init; }

	/// <summary>Ambient-occlusion texture (linear), R channel; often the same image as <see cref="MetallicRoughness"/>. Null when absent.</summary>
	public TextureData? Occlusion { get; init; }

	/// <summary>Emissive texture (sRGB), or null.</summary>
	public TextureData? Emissive { get; init; }

	/// <summary>Base-colour multiplier (linear RGBA); glTF default (1,1,1,1).</summary>
	public Vector4 BaseColorFactor { get; init; } = Vector4.One;

	/// <summary>Metallic multiplier; glTF default 1.</summary>
	public float MetallicFactor { get; init; } = 1f;

	/// <summary>Roughness multiplier; glTF default 1.</summary>
	public float RoughnessFactor { get; init; } = 1f;

	/// <summary>Emissive multiplier (linear RGB); glTF default (0,0,0).</summary>
	public Vector3 EmissiveFactor { get; init; } = Vector3.Zero;

	/// <summary>Occlusion strength (0 = no occlusion applied, 1 = full); glTF default 1.</summary>
	public float OcclusionStrength { get; init; } = 1f;

	/// <summary>Normal-map XY scale; glTF default 1.</summary>
	public float NormalScale { get; init; } = 1f;
}
