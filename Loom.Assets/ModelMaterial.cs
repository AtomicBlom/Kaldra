using Loom.Core.Materials;

namespace Loom.Assets;

/// <summary>A material referenced by a loaded primitive — its decoded textures, ready for <c>IEngine.CreateTexture</c>.</summary>
public sealed class ModelMaterial
{
	/// <summary>Base-colour / albedo texture (sRGB), or null if the material has none.</summary>
	public TextureData? BaseColor { get; init; }

	/// <summary>Tangent-space normal map (linear), or null if the material has none.</summary>
	public TextureData? Normal { get; init; }
}
