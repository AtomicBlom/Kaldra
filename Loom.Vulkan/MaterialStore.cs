using Loom.Core.Materials;

namespace Loom.Vulkan;

/// <summary>
/// Owns the texture-id list behind each <see cref="Material"/> handle. A material is just an ordered bundle of
/// textures (PlanningPhase2.md M11); the store records that order at <see cref="Create"/> and the draw path
/// resolves a material id to its texture ids, then to image views in the <see cref="TextureStore"/>. Kept
/// separate from the GPU resources so the material draw path binds with zero per-draw allocation.
/// </summary>
internal sealed class MaterialStore
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

	/// <summary>The ordered texture ids a material binds, for the draw path to resolve to image views.</summary>
	public ReadOnlySpan<int> TexturesOf(int materialId) => _materials[materialId];
}
