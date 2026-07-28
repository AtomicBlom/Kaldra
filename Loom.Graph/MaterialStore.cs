using Loom.Core.Materials;

namespace Loom.Graph;

/// <summary>
/// Owns what is behind each <see cref="Material"/> handle: its ordered texture ids, its per-material parameter bytes,
/// and — when it was created for a named technique — that pairing. A material is a bundle of neutral handles plus a
/// byte block, so this is pure bookkeeping and is shared rather than reimplemented per backend; the GPU resources stay
/// backend-side and the draw path resolves ids with zero per-draw allocation.
/// <para>
/// The parameter block lives here as a plain CPU array. Backends stage it into their existing per-frame uniform ring
/// during recording, so a material needs no GPU object of its own, no lifetime tracking, and no frames-in-flight
/// hazard — and <see cref="WriteParameters"/> is just a memory copy.
/// </para>
/// </summary>
public sealed class MaterialStore
{
	private sealed class Entry
	{
		public required int[] Textures { get; init; }
		public required byte[] Parameters { get; set; }
		public string? Technique { get; init; }

		// Techniques this material has already been validated against, so the draw path pays the check once per pair
		// rather than per draw. The entry for a technique is dropped when that technique is redefined (hot reload).
		public HashSet<string> Validated { get; } = [];
	}

	private readonly List<Entry> _materials = [];
	private readonly Dictionary<string, MaterialLayout> _layouts = [];

	/// <summary>Record a material's ordered texture ids (slot 0 → the shader's binding 0, …) and return its handle.</summary>
	public Material Create(ReadOnlySpan<Texture> textures) => Create(technique: null, textures, default);

	/// <summary>
	/// Record a material, optionally bound to <paramref name="technique"/> and carrying <paramref name="parameters"/>.
	/// When a technique is named, the pairing is validated at whichever of create-material and define-technique happens
	/// second, so the order the app does them in doesn't decide whether a mismatch is caught.
	/// </summary>
	public Material Create(string? technique, ReadOnlySpan<Texture> textures, ReadOnlySpan<byte> parameters)
	{
		var ids = new int[textures.Length];
		for (var i = 0; i < textures.Length; i++)
			ids[i] = textures[i].Id;

		var entry = new Entry { Textures = ids, Parameters = parameters.ToArray(), Technique = technique };
		var id = _materials.Count;
		_materials.Add(entry);
		var material = new Material(id);

		if (technique is not null && _layouts.TryGetValue(technique, out var layout))
			Validate(material, technique, layout, entry);
		return material;
	}

	/// <summary>The ordered texture ids a material binds, for the draw path to resolve to bound texture views.</summary>
	public ReadOnlySpan<int> TexturesOf(int materialId) => _materials[materialId].Textures;

	/// <summary>A material's per-material parameter bytes, for a backend to stage into its per-frame uniform ring. Empty when it carries none.</summary>
	public ReadOnlySpan<byte> ParametersOf(int materialId) => _materials[materialId].Parameters;

	/// <summary>
	/// Overwrite a material's parameter bytes in place. The length must match what it was created with — a material's
	/// block size is the shader contract it was validated against, so a different size is a different material.
	/// </summary>
	public void WriteParameters(int materialId, ReadOnlySpan<byte> parameters)
	{
		var entry = _materials[materialId];
		if (parameters.Length != entry.Parameters.Length)
		{
			throw new InvalidOperationException(
				$"Material {materialId} carries a {entry.Parameters.Length}-byte parameter block but {parameters.Length} bytes were written. " +
				(entry.Parameters.Length == 0
					? "This material was created without parameters; build it with CreateMaterial<T>(technique, in parameters, textures) to give it a block."
					: "Write the same struct the material was created with."));
		}
		parameters.CopyTo(entry.Parameters);
	}

	/// <summary>
	/// Record what a technique expects of a material, derived from its shader reflection — called by the engine on
	/// every <c>DefineTechnique</c>, including the replays a hot reload performs. Re-registering a name with a
	/// different layout revalidates every material built for it, so a shader edit that changes the contract is
	/// reported at the reload instead of shading from a stale binding.
	/// </summary>
	public void RegisterTechnique(string technique, MaterialLayout layout)
	{
		var changed = !_layouts.TryGetValue(technique, out var previous) || previous != layout;
		_layouts[technique] = layout;
		if (!changed)
			return;

		for (var id = 0; id < _materials.Count; id++)
		{
			var entry = _materials[id];
			entry.Validated.Remove(technique);
			if (entry.Technique == technique)
				Validate(new Material(id), technique, layout, entry);
		}
	}

	/// <summary>What a technique expects a material to supply, or null when the technique is not registered.</summary>
	public MaterialLayout? LayoutOf(string technique) => _layouts.TryGetValue(technique, out var layout) ? layout : null;

	/// <summary>
	/// Check that <paramref name="material"/> can drive <paramref name="technique"/>, memoized per pair so a repeated
	/// draw pays nothing. This is the one place the check lives, so all three backends reject the same mismatch with
	/// the same message rather than each raising its own backend exception — or, in one case, silently clamping to
	/// however many textures the material happened to carry.
	/// </summary>
	public void ValidateAgainst(Material material, string technique)
	{
		var entry = _materials[material.Id];
		if (entry.Validated.Contains(technique))
			return;
		if (_layouts.TryGetValue(technique, out var layout))
			Validate(material, technique, layout, entry);
		else
			entry.Validated.Add(technique); // no reflection for this technique (a built-in stand-in): nothing to check
	}

	private static void Validate(Material material, string technique, MaterialLayout layout, Entry entry)
	{
		if (layout.Accepts(entry.Textures.Length, entry.Parameters.Length))
		{
			entry.Validated.Add(technique);
			return;
		}

		var problem = entry.Textures.Length < layout.TextureCount
			? $"supplies {entry.Textures.Length} texture(s) but the technique samples {layout.TextureCount}"
			: $"carries a {entry.Parameters.Length}-byte parameter block but the technique reads {layout.ParameterByteSize} bytes";
		var hint = layout.HasParameters && entry.Parameters.Length == 0
			? $" The shader declares a '{ShaderReflection.MaterialParameterName}' block, so build the material with CreateMaterial<T>(\"{technique}\", in parameters, textures)."
			: " A technique's expectations come from its shader's reflection, so this is a disagreement between the shader and the material built for it.";
		throw new InvalidOperationException($"Material {material.Id} cannot drive technique '{technique}': it {problem}.{hint}");
	}
}
