using System.Collections.Immutable;
using System.Text;

using Loom.Core;

namespace Loom.Graph;

/// <summary>
/// The execution plan derived from a <see cref="RenderGraph"/>'s read/write edges: the passes in a valid
/// execution order, each annotated with the prerequisites it depends on. This is the first half of the
/// Execute layer — pure scheduling, no GPU types — so it stays in <c>Loom.Graph</c> and is fully
/// unit-testable. Barrier and layout-transition derivation (M3b) builds on the same per-resource timeline.
/// <para>
/// Dependencies are derived from same-resource access conflicts: read-after-write, write-after-read, and
/// write-after-write. Pure read-after-read needs no edge. Every edge points forward in program order, so
/// the dependency graph is a DAG and program order is always a valid topological order — the derivation's
/// product is the precise dependency/validation structure, not a reshuffle (that pays off later for async
/// queues, aliasing and culling).
/// </para>
/// </summary>
public sealed class GraphSchedule
{
	/// <summary>The source graph.</summary>
	public RenderGraph Graph { get; }

	/// <summary>The passes in derived execution order.</summary>
	public ImmutableArray<ScheduledPass> Passes { get; }

	private GraphSchedule(RenderGraph graph, ImmutableArray<ScheduledPass> passes)
	{
		Graph = graph;
		Passes = passes;
	}

	/// <summary>Per-resource scheduling state as the timeline is walked in program order.</summary>
	private sealed class ResourceState
	{
		public int LastWriter = -1;
		public readonly List<int> ReadersSinceWrite = [];
	}

	/// <summary>
	/// Derive the schedule. Throws <see cref="GraphValidationException"/> if a non-persistent resource is
	/// read before any pass writes it (it would sample undefined contents).
	/// </summary>
	public static GraphSchedule Derive(RenderGraph graph)
	{
		ArgumentNullException.ThrowIfNull(graph);
		var passes = graph.Passes;
		var n = passes.Length;

		var state = new Dictionary<int, ResourceState>(); // keyed by ResourceHandle.Id
		var dependsOn = new SortedSet<int>[n];
		var errors = new List<string>();

		ResourceState StateFor(int id)
		{
			if (!state.TryGetValue(id, out var s))
				state[id] = s = new ResourceState();
			return s;
		}

		for (var i = 0; i < n; i++)
		{
			dependsOn[i] = [];

			// Aggregate this pass's accesses per resource: a pass may touch a resource as both read and
			// write (e.g. .Blends), and we must resolve dependencies once, against state BEFORE this pass.
			var reads = new Dictionary<int, ResourceHandle>();
			var writes = new Dictionary<int, ResourceHandle>();
			foreach (var access in passes[i].Accesses)
			{
				if (access.Kind.Reads())
					reads[access.Resource.Id] = access.Resource;
				if (access.Kind.Writes())
					writes[access.Resource.Id] = access.Resource;
			}

			foreach (var (id, resource) in reads)
			{
				var s = state.GetValueOrDefault(id);
				if (s is { LastWriter: >= 0 })
					dependsOn[i].Add(s.LastWriter); // RAW
				else if (resource.Kind != ResourceKind.Persistent)
					errors.Add($"Pass '{passes[i].Name}' reads '{resource.Name}' before any pass writes it.");
			}

			foreach (var id in writes.Keys)
			{
				if (!state.TryGetValue(id, out var s))
					continue;
				if (s.ReadersSinceWrite.Count > 0)
				{
					// WAR: the readers are the resource's most recent usages (they already wait on the
					// prior writer), so they — not the writer — are the precise immediate prerequisites.
					foreach (var reader in s.ReadersSinceWrite)
						if (reader != i)
							dependsOn[i].Add(reader);
				}
				else if (s.LastWriter >= 0)
				{
					dependsOn[i].Add(s.LastWriter); // WAW
				}
			}

			// Commit this pass's effects to the timeline.
			foreach (var id in writes.Keys)
			{
				var s = StateFor(id);
				s.LastWriter = i;
				s.ReadersSinceWrite.Clear();
			}
			foreach (var id in reads.Keys)
			{
				if (writes.ContainsKey(id))
					continue; // a writer's own read is subsumed; it is the new last writer, not a reader
				StateFor(id).ReadersSinceWrite.Add(i);
			}
		}

		if (errors.Count > 0)
			throw new GraphValidationException(string.Join(Environment.NewLine, errors));

		var order = TopologicalOrder(n, dependsOn);

		var scheduled = ImmutableArray.CreateBuilder<ScheduledPass>(n);
		for (var orderIndex = 0; orderIndex < order.Count; orderIndex++)
		{
			var programIndex = order[orderIndex];
			scheduled.Add(new ScheduledPass(passes[programIndex], programIndex, orderIndex, [.. dependsOn[programIndex]]));
		}

		return new GraphSchedule(graph, scheduled.ToImmutable());
	}

	/// <summary>
	/// Kahn's algorithm with a program-order tiebreak: among passes whose prerequisites are all satisfied,
	/// always take the lowest program index. Deterministic, and reproduces program order when nothing forces
	/// otherwise. Cycles are structurally impossible (forward-only edges) but guarded defensively.
	/// </summary>
	private static List<int> TopologicalOrder(int n, SortedSet<int>[] dependsOn)
	{
		var inDegree = new int[n];
		var dependents = new List<int>[n];
		for (var i = 0; i < n; i++)
			dependents[i] = [];

		for (var i = 0; i < n; i++)
			foreach (var prerequisite in dependsOn[i])
			{
				inDegree[i]++;
				dependents[prerequisite].Add(i);
			}

		var ready = new SortedSet<int>();
		for (var i = 0; i < n; i++)
			if (inDegree[i] == 0)
				ready.Add(i);

		var order = new List<int>(n);
		while (ready.Count > 0)
		{
			var next = ready.Min;
			ready.Remove(next);
			order.Add(next);
			foreach (var dependent in dependents[next])
				if (--inDegree[dependent] == 0)
					ready.Add(dependent);
		}

		if (order.Count != n)
			throw new GraphValidationException("Render graph contains a dependency cycle (this should be impossible).");

		return order;
	}

	/// <summary>A human-readable dump of the execution order and derived dependencies, for inspection/tests.</summary>
	public string Dump()
	{
		var sb = new StringBuilder();
		sb.AppendLine($"GraphSchedule ({Passes.Length} passes, source hash {Graph.Hash()[..12]})");
		foreach (var scheduled in Passes)
		{
			var deps = scheduled.DependsOn.IsEmpty
				? "-"
				: string.Join(", ", scheduled.DependsOn.Select(d => Graph.Passes[d].Name));
			sb.AppendLine($"  {scheduled.Order}: {scheduled.Pass.Name}  [{scheduled.Pass.Queue}]  depends on: {deps}");
		}
		return sb.ToString();
	}
}
