namespace BinaryVibrance.Kaldra.Assets;

/// <summary>
/// Resolves a logical asset name (e.g. <c>"bloom_sample.spv"</c>) to a concrete file path, searching an ordered
/// list of <see cref="Roots"/> — highest priority first, the first root that has the file wins. Layering isn't
/// exercised yet (one root = the shader directory), but the contract supports it: a future resource pack is just a
/// higher-priority root. Carries no graphics-API types — it deals only in logical names, roots, and paths.
/// </summary>
public interface IAssetSource
{
	/// <summary>The search roots, highest priority first. Each is an absolute directory.</summary>
	IReadOnlyList<string> Roots { get; }

	/// <summary>The highest-priority root containing <paramref name="logicalName"/>, or false if none has it.</summary>
	bool TryResolve(string logicalName, out string fullPath);
}
