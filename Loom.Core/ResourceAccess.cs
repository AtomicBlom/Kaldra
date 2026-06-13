namespace Loom.Core;

/// <summary>
/// One edge in the graph: a pass touches a resource in a particular way. The complete set of these
/// across all passes is the only thing the Execute layer needs to derive ordering and barriers.
/// </summary>
/// <param name="Resource">The resource touched.</param>
/// <param name="Kind">How it is touched.</param>
public readonly record struct ResourceAccess(ResourceHandle Resource, AccessKind Kind);
