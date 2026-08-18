using System.Collections.Immutable;
using System.Text;
using Loom.Core.Diagnostics;
using Loom.Core.Graph;

namespace Loom.Graph;

/// <summary>
/// The execution plan derived from a <see cref="RenderGraph"/>'s read/write edges: the passes in a valid
/// execution order, each annotated with the prerequisites it depends on. This is the first half of the
/// Execute layer — pure scheduling, no GPU types — so it stays in <c>Loom.Graph</c> and is fully
/// unit-testable. Barrier and layout-transition derivation builds on the same per-resource timeline.
/// <para>
/// Dependencies are derived from same-resource access conflicts: read-after-write, write-after-read, and
/// write-after-write. Pure read-after-read needs no edge. Every edge points forward in program order, so
/// the dependency graph is a DAG and program order is always a valid topological order — the derivation's
/// product is the precise dependency/validation structure, not a reshuffle — the structure a future
/// multi-queue scheduler, the transient aliaser, and culling all build on.
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
	/// read before any pass writes it (it would sample undefined contents), if the present target is never
	/// written, or if a pass samples or read-writes the backbuffer (a write/present target only). Every such
	/// problem in the graph is reported together rather than one per attempt. Also emits a console warning for
	/// any dead pass (writes nothing consumed/presented/read back).
	/// </summary>
	public static GraphSchedule Derive(RenderGraph graph)
	{
		ArgumentNullException.ThrowIfNull(graph);
		var passes = graph.Passes;
		var n = passes.Length;

		// Async compute / multi-queue is not yet derived or backed: the scheduler below builds one linear timeline
		// with a single ResourceState per resource, and both backends create one queue. Fail loud rather than
		// silently running an AsyncCompute pass on the graphics queue and implying an overlap that never happens.
		foreach (var pass in passes)
		{
			if (pass.Queue == Queue.AsyncCompute)
			{
				throw new GraphValidationException(
					$"Pass '{pass.Name}' requests Queue.AsyncCompute, but Loom does not yet derive cross-queue scheduling and both backends are single-queue. Drop the .Queue(...) assignment until multi-queue lands.");
			}
		}

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
			// Uniform and storage reads bind to numbered technique slots, and there are only so many. Counted here
			// because every backend collects these edges the same way and every one of them used to drop the overflow
			// silently, mis-binding each slot after the cap rather than reporting anything.
			var uniformReads = 0;
			var storageReads = 0;
			foreach (var access in passes[i].Accesses)
			{
				if (access.Kind == AccessKind.ReadUniform)
					uniformReads++;
				else if (access.Kind == AccessKind.ReadStorage)
					storageReads++;

				// The swapchain image is a write-and-present target only. It is acquired fresh each frame, so it has
				// no stable contents to sample, and each backend resolves it differently when asked to: one binds
				// whichever image was last acquired, one hands back a sentinel view, one throws while recording.
				// Reject it here so the answer is the same everywhere, and before a frame is ever recorded.
				if (access.Resource.Kind == ResourceKind.Backbuffer && access.Kind is AccessKind.Sample or AccessKind.ReadWrite)
				{
					var verb = access.Kind == AccessKind.Sample ? ".Samples" : ".ReadWrites";
					errors.Add(
						$"Pass '{passes[i].Name}' declares {verb} on the backbuffer, which is a write/present target only. " +
						"Render into a target declared on g.Screen and write that to the backbuffer instead.");
				}

				if (access.Kind.Reads())
					reads[access.Resource.Id] = access.Resource;
				if (access.Kind.Writes())
					writes[access.Resource.Id] = access.Resource;
			}

			if (uniformReads > GraphLimits.MaxUniformBuffersPerPass)
				errors.Add(
					$"Pass '{passes[i].Name}' reads {uniformReads} constants buffers but a pass may read at most " +
					$"{GraphLimits.MaxUniformBuffersPerPass}. Pack the extras into one larger block, or split the pass.");
			if (storageReads > GraphLimits.MaxStorageBuffersPerPass)
				errors.Add(
					$"Pass '{passes[i].Name}' reads {storageReads} storage buffers but a pass may read at most " +
					$"{GraphLimits.MaxStorageBuffersPerPass}. Pack the extras into one larger buffer, or split the pass.");

			ValidateAttachments(passes[i], errors);

			foreach (var (id, resource) in reads)
			{
				var s = state.GetValueOrDefault(id);
				if (s is { LastWriter: >= 0 })
					dependsOn[i].Add(s.LastWriter); // RAW
				else if (resource.Kind is not (ResourceKind.Persistent or ResourceKind.Constants or ResourceKind.Storage) && !writes.ContainsKey(id))
					// A pure read of a never-written, non-persistent resource samples undefined contents. A
					// read+write in the SAME pass (a .ReadWrites producer, e.g. the particle compute) is the
					// producer, not a consumer of undefined data — its own write initialises it, so it's fine.
					// Constants and Storage are CPU-written by the app (IEngine.Write), so they have no graph writer.
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

		// The present target must be produced by some pass; otherwise the screen would show undefined contents.
		if (graph.PresentTarget is { } present
			&& (!state.TryGetValue(present.Id, out var presentState) || presentState.LastWriter < 0))
			errors.Add($"Present target '{present.Name}' is never written by any pass.");

		// And the converse: a backbuffer that a pass writes but nothing presents renders a whole frame and then drops
		// it, leaving a permanently blank window with no diagnostic anywhere. There is no legitimate reason to do it —
		// the backbuffer handle only exists because the app asked for it — so this is an error, not a warning. An
		// offscreen graph never mints one, so it is unaffected.
		foreach (var resource in graph.Resources)
		{
			if (!resource.IsBackbuffer
				|| !state.TryGetValue(resource.Id, out var backbufferState)
				|| backbufferState.LastWriter < 0
				|| graph.PresentTarget?.Id == resource.Id)
				continue;

			var writer = passes[backbufferState.LastWriter].Name;
			errors.Add(graph.PresentTarget is { } other
				? $"Pass '{writer}' writes the backbuffer '{resource.Name}', but the graph presents '{other.Name}' instead — the backbuffer's contents would never reach the window."
				: $"Pass '{writer}' writes the backbuffer '{resource.Name}', but the graph presents nothing — call g.Present(...) on it, or the window stays blank.");
		}

		if (errors.Count > 0)
			throw new GraphValidationException(string.Join(Environment.NewLine, errors));

		WarnDeadPasses(graph, dependsOn);

		var order = TopologicalOrder(n, dependsOn);

		var scheduled = ImmutableArray.CreateBuilder<ScheduledPass>(n);
		for (var orderIndex = 0; orderIndex < order.Count; orderIndex++)
		{
			var programIndex = order[orderIndex];
			scheduled.Add(new ScheduledPass(passes[programIndex], programIndex, orderIndex, [.. dependsOn[programIndex]]));
		}

		return new GraphSchedule(graph, scheduled.ToImmutable());
	}

	// The three things every backend needs to be true of a raster pass's attachments before it can begin a render
	// scope for it, checked here because the backends checked them inconsistently or not at all.
	private static void ValidateAttachments(GraphPass pass, List<string> errors)
	{
		if (pass.Kind != PassKind.Raster)
			return; // a compute pass has no attachments and no render scope

		// (1) One pixel extent. A pass renders through one viewport, scissor and render area. For a graph that means
		// one domain at one scale: equal domain and equal scale resolve to equal pixels whatever the swapchain size,
		// which a comparison of pixel extents could not promise here (a backbuffer-relative domain has no extent until
		// bake).
		//
		// Nothing checked this. Each backend instead picked a single extent out of the pass's attachments and they did
		// not agree on which — two took the first attachment, one took the last colour attachment — so a pass mixing
		// extents rendered at a different size per backend, and every one of those sizes is wrong for at least one of
		// its attachments. There is no correct single answer to pick, so the declaration is what has to go.
		ResourceHandle? anchor = null;
		var mixedExtents = false;
		var colorAttachments = 0;
		var attachments = 0;

		foreach (var access in pass.Accesses)
		{
			if (!IsAttachmentKind(access.Kind))
				continue;
			attachments++;
			if (access.Kind is AccessKind.Write or AccessKind.Blend)
				colorAttachments++;

			var other = access.Resource;
			if (anchor is null)
			{
				anchor = other;
				continue;
			}
			if (mixedExtents || (ReferenceEquals(anchor.Domain, other.Domain) && anchor.Scale == other.Scale))
				continue;
			mixedExtents = true; // one report per pass — listing every pair says nothing more
			errors.Add(
				$"Pass '{pass.Name}' attaches '{anchor.Name}' ({anchor.Domain.Name} × {anchor.Scale}) and '{other.Name}' " +
				$"({other.Domain.Name} × {other.Scale}), which resolve to different pixel extents. A pass renders " +
				"through one viewport and render area, so its colour and depth attachments must share a domain and a " +
				"scale. Declare them in the same domain, or split the pass.");
		}

		// (2) Something to render into. DirectX 12 threw for this while recording; Vulkan and WebGPU began a 0x0 pass
		// and drew nothing, which looks exactly like a pass whose draws are broken.
		if (attachments == 0)
		{
			errors.Add(
				$"Pass '{pass.Name}' is a raster pass with no colour or depth attachment, so it has nothing to render " +
				"into and no extent to render at. Declare a target with .Writes/.Blends/.WritesDepth, or make it a " +
				"compute pass with g.Compute.");
		}

		// (3) The portable colour-attachment ceiling. Each backend guarded this at record time, at its own number and
		// inconsistently between its own two guards — so a pass could derive everywhere and then fail on one backend.
		if (colorAttachments > GraphLimits.MaxColorAttachmentsPerPass)
		{
			errors.Add(
				$"Pass '{pass.Name}' writes {colorAttachments} colour attachments but a pass may write at most " +
				$"{GraphLimits.MaxColorAttachmentsPerPass} — the portable ceiling (D3D12's simultaneous render targets " +
				"and WebGPU's maxColorAttachments). Pack the outputs into fewer targets, or split the pass.");
		}
	}

	// The access kinds that make a resource an attachment of a raster pass, matching what BarrierPlan classifies as
	// one: the colour writes, the accumulating write, and both depth uses.
	private static bool IsAttachmentKind(AccessKind kind) =>
		kind is AccessKind.Write or AccessKind.Blend or AccessKind.WriteDepth or AccessKind.ReadDepth;

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

	/// <summary>
	/// Surface passes whose writes nobody reads downstream and which aren't the presented or read-back output —
	/// wasted GPU work worth flagging. A <em>warning</em>, not an error: a dead pass is
	/// suboptimal, not incorrect, so we don't fail the build over it.
	/// </summary>
	private static void WarnDeadPasses(RenderGraph graph, SortedSet<int>[] dependsOn)
	{
		var dependedUpon = new HashSet<int>();
		foreach (var deps in dependsOn)
			dependedUpon.UnionWith(deps);

		var outputIds = new HashSet<int>(graph.ReadbackTargets.Select(r => r.Id));
		if (graph.PresentTarget is { } present)
			outputIds.Add(present.Id);

		for (var i = 0; i < graph.Passes.Length; i++)
		{
			if (dependedUpon.Contains(i))
				continue;

			var writesAnything = false;
			var feedsOutput = false;
			foreach (var access in graph.Passes[i].Accesses)
			{
				if (access.Kind.Writes())
					writesAnything = true;
				if (outputIds.Contains(access.Resource.Id))
					feedsOutput = true;
			}

			if (writesAnything && !feedsOutput)
				LoomLog.Warning($"pass '{graph.Passes[i].Name}' produces nothing read downstream, presented, or read back — it may be dead.");
		}
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

	/// <summary>
	/// A GraphViz DOT rendering of the <em>derived</em> execution DAG: one node per pass in
	/// execution order (ranked top→bottom), with an edge from each prerequisite to the pass that depends on it.
	/// This is the scheduling story — what <see cref="RenderGraph.ToDot"/>'s data-flow view derives into. Pairs
	/// with it; render with <c>dot -Tpng</c>. Deterministic (execution order).
	/// </summary>
	public string ToDot()
	{
		var sb = new StringBuilder();
		sb.AppendLine("digraph GraphSchedule {");
		sb.AppendLine("  rankdir=TB;");
		sb.AppendLine("  node [shape=box, style=\"filled,rounded\", fontname=\"Segoe UI\"];");
		sb.AppendLine($"  labelloc=\"t\"; label=\"GraphSchedule ({Passes.Length} passes, source hash {Graph.Hash()[..12]})\";");

		foreach (var scheduled in Passes)
		{
			var fill = scheduled.Pass.Kind == PassKind.Compute ? "#fff2b3" : "#d9d9d9";
			sb.AppendLine($"  \"{scheduled.Pass.Name}\" [fillcolor=\"{fill}\", label=\"{scheduled.Order}: {scheduled.Pass.Name}\\n[{scheduled.Pass.Queue}]\"];");
		}

		foreach (var scheduled in Passes)
			foreach (var dep in scheduled.DependsOn)
				sb.AppendLine($"  \"{Graph.Passes[dep].Name}\" -> \"{scheduled.Pass.Name}\";");

		sb.AppendLine("}");
		return sb.ToString();
	}
}
