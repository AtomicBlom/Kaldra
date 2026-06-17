namespace Loom.Assets;

/// <summary>
/// An <see cref="IAssetSource"/> over an ordered list of directory roots — the simplest layering policy: the first
/// root (highest priority) that contains a logical name wins, so a later "resource pack" root can override a base
/// asset just by sitting earlier in the list. M13 uses a single root (the shader directory).
/// </summary>
public sealed class LayeredAssetSource : IAssetSource
{
	private readonly string[] _roots;

	/// <summary>Create a source over <paramref name="roots"/>, highest priority first. Empty/null roots are dropped;
	/// the rest are normalised to absolute paths.</summary>
	public LayeredAssetSource(params string[] roots) =>
		_roots = [.. roots.Where(r => !string.IsNullOrWhiteSpace(r)).Select(Path.GetFullPath)];

	public IReadOnlyList<string> Roots => _roots;

	public bool TryResolve(string logicalName, out string fullPath)
	{
		foreach (var root in _roots)
		{
			var candidate = Path.Combine(root, logicalName);
			if (File.Exists(candidate))
			{
				fullPath = candidate;
				return true;
			}
		}
		fullPath = string.Empty;
		return false;
	}
}
