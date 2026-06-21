namespace Loom.Core.Graph;

/// <summary>Whether a pass records graphics draws or a compute dispatch.</summary>
public enum PassKind
{
	/// <summary>A raster pass: <c>g.Pass(...)</c> ending in <c>.Draw</c>.</summary>
	Raster,

	/// <summary>A compute pass: <c>g.Compute(...)</c> ending in <c>.Dispatch</c>.</summary>
	Compute,
}
