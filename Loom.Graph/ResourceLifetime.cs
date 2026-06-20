using Loom.Core;

namespace Loom.Graph;

/// <summary>
/// The execution-order span over which a resource is live: from the first pass that touches it to the last,
/// measured in <see cref="ScheduledPass.Order"/> units. Two transients whose spans do <em>not</em> overlap
/// can share the same physical memory — the basis of transient aliasing. The span is
/// inclusive at both ends; a resource live at order <c>k</c> occupies that whole pass, so two resources that
/// share any order overlap and must not alias.
/// </summary>
/// <param name="Resource">The resource this lifetime describes.</param>
/// <param name="FirstUse">Order of the earliest pass that accesses the resource.</param>
/// <param name="LastUse">Order of the latest pass that accesses the resource.</param>
/// <param name="Aliasable">
/// True if the resource's memory may be recycled. False for <see cref="ResourceKind.Persistent"/> (lifetime
/// is forever — cross-frame history) and the backbuffer (swapchain-owned, not allocated by Loom).
/// </param>
public readonly record struct ResourceLifetime(ResourceHandle Resource, int FirstUse, int LastUse, bool Aliasable)
{
	/// <summary>Do the two live spans share any execution-order slot? Overlapping spans cannot alias.</summary>
	public bool Overlaps(in ResourceLifetime other) => FirstUse <= other.LastUse && other.FirstUse <= LastUse;

	/// <summary>May these two resources share memory — both recyclable and never live at the same time?</summary>
	public bool CanAlias(in ResourceLifetime other) => Aliasable && other.Aliasable && !Overlaps(in other);
}
