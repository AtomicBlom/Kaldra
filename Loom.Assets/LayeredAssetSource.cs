namespace Loom.Assets;

/// <summary>
/// An <see cref="IAssetSource"/> over an ordered list of directory roots — the simplest layering policy: the first
/// root (highest priority) that contains a logical name wins, so a later "resource pack" root can override a base
/// asset just by sitting earlier in the list.
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
		fullPath = string.Empty;
		// Logical names are leaf-relative. Reject a rooted/absolute name, and (after Path.GetFullPath resolves any
		// "..") require the result to stay under the root — so a name like "..\..\secret" can't escape the layered
		// roots and read an arbitrary file, silently defeating layering priority.
		if (string.IsNullOrEmpty(logicalName) || Path.IsPathRooted(logicalName))
			return false;
		foreach (var root in _roots)
		{
			var candidate = Path.GetFullPath(Path.Combine(root, logicalName));
			if (!candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
				continue; // escaped this root via traversal — skip it
			if (File.Exists(candidate))
			{
				fullPath = candidate;
				return true;
			}
		}
		return false;
	}
}
