using System.Collections.Immutable;
using System.Text;

using Loom.Core;

namespace Loom.Graph;

/// <summary>
/// Per-transient lifetime analysis derived from a <see cref="GraphSchedule"/>: for each resource, the
/// execution-order span over which it is live (first touch to last touch). This is the input the backend's
/// transient aliaser needs to recycle memory across non-overlapping lifetimes —
/// the classic "the G-buffer's memory is reused by the bloom mips once the G-buffer is consumed" case.
/// <para>
/// Backend-neutral by design: it yields abstract <see cref="ResourceLifetime"/> spans and an overlap/alias
/// query, but makes no placement decision (offsets, pools, real memory requirements) — that is the backend's
/// job, because aliasing two images also needs an aliasing memory barrier at the hand-off, which only the
/// backend emits. A resource declared but never used by any pass gets no entry (nothing to allocate).
/// </para>
/// </summary>
public sealed class LifetimePlan
{
	/// <summary>The source schedule.</summary>
	public GraphSchedule Schedule { get; }

	/// <summary>One lifetime per resource that at least one pass touches, ordered by <see cref="ResourceHandle.Id"/>.</summary>
	public ImmutableArray<ResourceLifetime> Lifetimes { get; }

	private LifetimePlan(GraphSchedule schedule, ImmutableArray<ResourceLifetime> lifetimes)
	{
		Schedule = schedule;
		Lifetimes = lifetimes;
	}

	/// <summary>Derive the lifetime spans for a schedule.</summary>
	public static LifetimePlan Derive(GraphSchedule schedule)
	{
		ArgumentNullException.ThrowIfNull(schedule);

		// Walk passes in execution order, widening each touched resource's [first,last] span.
		var first = new Dictionary<int, int>();
		var last = new Dictionary<int, int>();
		var handles = new Dictionary<int, ResourceHandle>();

		foreach (var scheduled in schedule.Passes)
		{
			foreach (var id in DistinctResourceIds(scheduled.Pass, handles))
			{
				if (!first.ContainsKey(id))
					first[id] = scheduled.Order;
				last[id] = scheduled.Order; // passes visited in ascending order, so this is the latest so far
			}
		}

		// A g.Readback target is copied to the host AFTER every pass (end of frame), but that copy is not a
		// pass, so the walk above stopped its lifetime at its last graph use. Extend it past the final pass so
		// the aliaser never recycles its memory before the copy-out — otherwise the readback samples whatever
		// later transient aliased into it (picking reads garbage; the hover outline flickers).
		var endOfFrame = schedule.Passes.Length; // pass orders are 0..n-1, so n is strictly after them all
		foreach (var target in schedule.Graph.ReadbackTargets)
			if (last.ContainsKey(target.Id))
				last[target.Id] = endOfFrame;

		var lifetimes = ImmutableArray.CreateBuilder<ResourceLifetime>(first.Count);
		foreach (var (id, handle) in handles.OrderBy(kv => kv.Key))
			lifetimes.Add(new ResourceLifetime(handle, first[id], last[id], IsAliasable(handle)));

		return new LifetimePlan(schedule, lifetimes.ToImmutable());
	}

	/// <summary>The lifetime of a specific resource, or null if no pass touches it.</summary>
	public ResourceLifetime? Of(ResourceHandle resource)
	{
		ArgumentNullException.ThrowIfNull(resource);
		foreach (var lifetime in Lifetimes)
			if (lifetime.Resource.Id == resource.Id)
				return lifetime;
		return null;
	}

	// Only transient render targets are recyclable. Persistent resources live forever (cross-frame history);
	// the backbuffer is owned by the swapchain, not allocated here.
	private static bool IsAliasable(ResourceHandle resource) =>
		resource.Kind is ResourceKind.Color or ResourceKind.Depth;

	private static IEnumerable<int> DistinctResourceIds(GraphPass pass, Dictionary<int, ResourceHandle> handles)
	{
		var seen = new HashSet<int>();
		foreach (var access in pass.Accesses)
		{
			if (!seen.Add(access.Resource.Id))
				continue;
			handles[access.Resource.Id] = access.Resource;
			yield return access.Resource.Id;
		}
	}

	/// <summary>A human-readable dump of the lifetime spans, for inspection and tests.</summary>
	public string Dump()
	{
		var sb = new StringBuilder();
		sb.AppendLine($"LifetimePlan ({Lifetimes.Length} resources, source hash {Schedule.Graph.Hash()[..12]})");
		foreach (var lifetime in Lifetimes)
		{
			var alias = lifetime.Aliasable ? "" : "  (not aliasable)";
			sb.AppendLine($"  [{lifetime.Resource.Id}] {lifetime.Resource.Name}: orders {lifetime.FirstUse}..{lifetime.LastUse}{alias}");
		}
		return sb.ToString();
	}
}
