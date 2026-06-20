using Loom.Core.Materials;

namespace Loom.DX12;

/// <summary>
/// Owns the texture-id list behind each <see cref="Material"/> handle — the DX12 counterpart of the Vulkan
/// backend's <see cref="MaterialStore"/>. A material is an ordered bundle of textures (PlanningPhase2.md M11);
/// the draw path resolves a material id to its texture ids, then to SRVs over the <see cref="DX12TextureStore"/>.
/// </summary>
internal sealed class DX12MaterialStore
{
	private readonly List<int[]> _materials = [];

	/// <summary>Record a material's ordered texture ids (slot 0 → the shader's binding 0, …) and return its handle.</summary>
	public Material Create(ReadOnlySpan<Texture> textures)
	{
		var ids = new int[textures.Length];
		for (var i = 0; i < textures.Length; i++)
			ids[i] = textures[i].Id;
		var id = _materials.Count;
		_materials.Add(ids);
		return new Material(id);
	}

	/// <summary>The ordered texture ids a material binds, for the draw path to resolve to SRVs.</summary>
	public ReadOnlySpan<int> TexturesOf(int materialId) => _materials[materialId];
}
