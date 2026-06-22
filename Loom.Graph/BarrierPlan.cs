using System.Collections.Immutable;
using System.Text;
using Loom.Core.Graph;

namespace Loom.Graph;

/// <summary>
/// The second half of the Execute layer: from a <see cref="GraphSchedule"/>, derive the image-layout
/// transitions and attachment load/store actions for every pass — the barriers the user never writes
/// Backend-neutral, so the same plan drives both the Vulkan and DirectX12 backends.
/// <para>
/// It walks each resource's <see cref="ResourceState"/> timeline in execution order. A transition is
/// emitted before a pass whenever the required state differs from the resource's current state (a layout
/// change) or the pass writes the resource (a write always hazards with the prior usage, even at the same
/// layout). Pure read-after-read needs nothing. The present target gets a terminal transition to
/// <see cref="ResourceState.Present"/>.
/// </para>
/// </summary>
public sealed class BarrierPlan
{
	/// <summary>Per-pass synchronisation, in execution order (aligned with the schedule).</summary>
	public ImmutableArray<PassBarriers> Passes { get; }

	/// <summary>Transitions to emit after the last pass — currently the present target to <see cref="ResourceState.Present"/>.</summary>
	public ImmutableArray<ResourceTransition> FinalTransitions { get; }

	/// <summary>Debug-label groups still open after the last pass — the backend closes this many before the frame ends (see <see cref="DebugLabelScopes"/>).</summary>
	public int TrailingLabelCloses { get; }

	private BarrierPlan(ImmutableArray<PassBarriers> passes, ImmutableArray<ResourceTransition> finalTransitions, int trailingLabelCloses)
	{
		Passes = passes;
		FinalTransitions = finalTransitions;
		TrailingLabelCloses = trailingLabelCloses;
	}

	private readonly record struct Use(ResourceHandle Resource, ResourceState State, bool IsAttachment, bool IsBlend, bool IsReadOnlyAttachment);

	/// <summary>Derive the barrier plan for a schedule.</summary>
	public static BarrierPlan Derive(GraphSchedule schedule)
	{
		ArgumentNullException.ThrowIfNull(schedule);
		var ordered = schedule.Passes;

		// First walk: for each resource, the execution-order positions where it is used (for store + reuse decisions).
		var usageOrders = new Dictionary<int, List<int>>();
		foreach (var scheduled in ordered)
			foreach (var id in DistinctResourceIds(scheduled.Pass))
				(usageOrders.TryGetValue(id, out var list) ? list : usageOrders[id] = []).Add(scheduled.Order);

		var presentId = schedule.Graph.PresentTarget?.Id ?? -1;

		// Derive the nested GPU debug-label scopes from the dotted pass names (diagnostic-only, computed once).
		var labels = DebugLabelScopes.Derive([.. ordered.Select(s => s.Pass.Name)]);

		// Second walk: track each resource's current state and emit transitions + attachment actions.
		var currentState = new Dictionary<int, ResourceState>();
		var seen = new HashSet<int>();
		var passBarriers = ImmutableArray.CreateBuilder<PassBarriers>(ordered.Length);

		for (var idx = 0; idx < ordered.Length; idx++)
		{
			var scheduled = ordered[idx];
			var transitions = ImmutableArray.CreateBuilder<ResourceTransition>();
			var attachments = ImmutableArray.CreateBuilder<AttachmentAction>();

			foreach (var use in UsesOf(scheduled.Pass))
			{
				var id = use.Resource.Id;
				var from = currentState.GetValueOrDefault(id, ResourceState.Undefined);

				if (from != use.State || IsWritingState(use.State))
					transitions.Add(new ResourceTransition(use.Resource, from, use.State));

				if (use.IsAttachment)
				{
					var first = !seen.Contains(id);
					var load = (use.IsReadOnlyAttachment, use.IsBlend, first) switch
					{
						(true, _, _) => LoadAction.Load,   // depth-read: existing depth is the input
						(_, true, _) => LoadAction.Load,   // blend: accumulate onto existing contents
						(_, _, true) => LoadAction.Clear,  // first write of a fresh target
						_ => LoadAction.Load,               // write over an already-populated target
					};
					var store = use.IsReadOnlyAttachment
						? StoreAction.DontCare
						: (UsedAfter(usageOrders, id, scheduled.Order) || id == presentId ? StoreAction.Store : StoreAction.DontCare);
					attachments.Add(new AttachmentAction(use.Resource, use.State, load, store));
				}

				currentState[id] = use.State;
				seen.Add(id);
			}

			var label = labels.Passes[idx];
			passBarriers.Add(new PassBarriers(scheduled.Pass, scheduled.Order, transitions.ToImmutable(), attachments.ToImmutable(), label.Closes, label.Opens, label.Leaf));
		}

		var finals = ImmutableArray.CreateBuilder<ResourceTransition>();
		if (schedule.Graph.PresentTarget is { } present)
		{
			var from = currentState.GetValueOrDefault(present.Id, ResourceState.Undefined);
			finals.Add(new ResourceTransition(present, from, ResourceState.Present));
		}

		return new BarrierPlan(passBarriers.ToImmutable(), finals.ToImmutable(), labels.TrailingCloses);
	}

	private static bool IsWritingState(ResourceState state) =>
		state is ResourceState.ColorAttachment or ResourceState.DepthAttachment or ResourceState.Storage;

	private static bool UsedAfter(Dictionary<int, List<int>> usageOrders, int id, int order) =>
		usageOrders.TryGetValue(id, out var list) && list.Exists(o => o > order);

	private static IEnumerable<int> DistinctResourceIds(GraphPass pass)
	{
		var seen = new HashSet<int>();
		foreach (var access in pass.Accesses)
			if (seen.Add(access.Resource.Id))
				yield return access.Resource.Id;
	}

	/// <summary>
	/// Reduce a pass's accesses to one <see cref="Use"/> per resource. A resource is normally touched one
	/// way per pass; if not, depth/storage/colour win over a plain sample (the binding that needs a layout).
	/// </summary>
	private static IEnumerable<Use> UsesOf(GraphPass pass)
	{
		var flags = new Dictionary<int, (ResourceHandle Res, bool Write, bool Blend, bool Sample, bool WriteDepth, bool ReadDepth, bool Storage, bool Vertex)>();
		foreach (var access in pass.Accesses)
		{
			// An app-written parameter buffer is host-coherent and needs no layout/state transition — it never
			// produces a Use, so it stays out of barrier derivation entirely (the bind is handled at record time).
			if (access.Kind == AccessKind.ReadUniform)
				continue;
			var f = flags.GetValueOrDefault(access.Resource.Id);
			f.Res = access.Resource;
			switch (access.Kind)
			{
				case AccessKind.Write: f.Write = true; break;
				case AccessKind.Blend: f.Blend = true; break;
				case AccessKind.Sample: f.Sample = true; break;
				case AccessKind.WriteDepth: f.WriteDepth = true; break;
				case AccessKind.ReadDepth: f.ReadDepth = true; break;
				case AccessKind.ReadWrite: f.Storage = true; break;
				case AccessKind.ReadVertex: f.Vertex = true; break;
			}
			flags[access.Resource.Id] = f;
		}

		return flags.Values
			.OrderBy(f => f.Res.Id)
			.Select(f =>
			{
				// A handle both sampled and colour-written by the SAME pass is a GPU feedback hazard Loom does not
				// model (the priority below would silently drop the read). Reject it at derive time rather than
				// mis-deriving the barrier — use two passes, or a ping-pong handle pair.
				if (f.Sample && (f.Write || f.Blend))
				{
					throw new GraphValidationException(
						$"Resource '{f.Res.Name}' is both sampled and written (.Writes/.Blends) by the same pass — an unsupported in-pass feedback hazard.");
				}
				if (f.WriteDepth)
					return new Use(f.Res, ResourceState.DepthAttachment, IsAttachment: true, IsBlend: false, IsReadOnlyAttachment: false);
				if (f.ReadDepth)
					return new Use(f.Res, ResourceState.DepthReadOnly, IsAttachment: true, IsBlend: false, IsReadOnlyAttachment: true);
				if (f.Storage)
					return new Use(f.Res, ResourceState.Storage, IsAttachment: false, IsBlend: false, IsReadOnlyAttachment: false);
				if (f.Vertex)
					return new Use(f.Res, ResourceState.VertexInput, IsAttachment: false, IsBlend: false, IsReadOnlyAttachment: false);
				if (f.Write || f.Blend)
					return new Use(f.Res, ResourceState.ColorAttachment, IsAttachment: true, IsBlend: f.Blend && !f.Write, IsReadOnlyAttachment: false);
				return new Use(f.Res, ResourceState.ShaderRead, IsAttachment: false, IsBlend: false, IsReadOnlyAttachment: false);
			});
	}

	/// <summary>A human-readable dump of the derived barriers, for inspection and tests.</summary>
	public string Dump()
	{
		var sb = new StringBuilder();
		sb.AppendLine($"BarrierPlan ({Passes.Length} passes)");
		foreach (var pass in Passes)
		{
			sb.AppendLine($"  {pass.Order}: {pass.Pass.Name}");
			foreach (var t in pass.Transitions)
				sb.AppendLine($"      barrier  {t.Resource.Name}: {t.From} -> {t.To}");
			foreach (var a in pass.Attachments)
				sb.AppendLine($"      attach   {a.Resource.Name} ({a.State})  load={a.Load} store={a.Store}");
		}
		foreach (var t in FinalTransitions)
			sb.AppendLine($"  final barrier  {t.Resource.Name}: {t.From} -> {t.To}");
		return sb.ToString();
	}
}
