using System.Numerics;
using System.Runtime.InteropServices;

namespace Loom.Assets;

/// <summary>
/// A glTF metallic-roughness material's scalar factors, laid out for a shader's per-material parameter block — the
/// GPU-side counterpart of the factor half of <see cref="ModelMaterial"/>. Hand it to
/// <c>IEngine.CreateMaterial&lt;T&gt;</c> (which <see cref="MaterialUploader"/> does) and the engine stages it into
/// the block a shader declares as <c>loomMaterial</c>.
/// <para>
/// This lives in <c>Loom.Assets</c>, beside the loader that produces it, rather than in the engine — the engine names
/// no material model, and a PBR struct in <c>Loom.Core</c> would be exactly that. It is one possible shape for the
/// parameter channel, the one glTF happens to imply; an app with its own surface model declares its own struct and
/// the engine is equally happy.
/// </para>
/// <para>
/// <b>Layout matters.</b> Every member is a 16-byte-aligned <see cref="Vector4"/> because HLSL constant-buffer
/// packing and WGSL uniform layout agree on those and disagree on tighter ones — a bare <c>float3</c> or a trailing
/// <c>float</c> would land at different offsets on different backends. That is why the three loose scalars are
/// packed into <see cref="MetallicRoughnessNormalOcclusion"/> rather than given fields of their own, and why the
/// alpha lane of <see cref="Emissive"/> is padding. 48 bytes total.
/// </para>
/// </summary>
/// <param name="BaseColor">Base-colour multiplier (linear RGBA). The shader multiplies it by the sampled albedo, so a material with no base-colour texture shades from this alone — matching glTF's "texture absent means the factor applies unmodified".</param>
/// <param name="Emissive">Emissive multiplier in <c>rgb</c>; <c>w</c> is padding and carries no meaning.</param>
/// <param name="MetallicRoughnessNormalOcclusion">The four loose scalars, in order: metallic, roughness, normal-map scale, occlusion strength.</param>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct GltfMaterialFactors(
	Vector4 BaseColor,
	Vector4 Emissive,
	Vector4 MetallicRoughnessNormalOcclusion)
{
	/// <summary>The glTF defaults: white base colour, no emission, fully metallic and fully rough, unscaled normals, full occlusion.</summary>
	public static GltfMaterialFactors Default => From(new ModelMaterial());

	/// <summary>Metallic multiplier.</summary>
	public float Metallic => MetallicRoughnessNormalOcclusion.X;

	/// <summary>Roughness multiplier.</summary>
	public float Roughness => MetallicRoughnessNormalOcclusion.Y;

	/// <summary>Normal-map XY scale.</summary>
	public float NormalScale => MetallicRoughnessNormalOcclusion.Z;

	/// <summary>Occlusion strength.</summary>
	public float OcclusionStrength => MetallicRoughnessNormalOcclusion.W;

	/// <summary>Pack a loaded material's factors into the block, dropping its textures (which upload separately).</summary>
	public static GltfMaterialFactors From(ModelMaterial material)
	{
		ArgumentNullException.ThrowIfNull(material);
		return new GltfMaterialFactors(
			material.BaseColorFactor,
			new Vector4(material.EmissiveFactor, 0f),
			new Vector4(material.MetallicFactor, material.RoughnessFactor, material.NormalScale, material.OcclusionStrength));
	}
}
