using System.Collections.Immutable;

using Loom.Core;

namespace Loom.Graph;

/// <summary>
/// A pass placed in execution order, carrying the prerequisite passes derived from its read/write edges.
/// <see cref="DependsOn"/> holds <em>immediate</em> prerequisites (the last writer for a read; the last
/// writer plus all readers-since for a write) — exactly what barrier derivation (M3b) will turn into
/// <c>vkCmdPipelineBarrier2</c> waits. Transitive ordering is handled by the schedule's sort.
/// </summary>
public sealed class ScheduledPass
{
	/// <summary>The described pass.</summary>
	public GraphPass Pass { get; }

	/// <summary>This pass's index in the original program (declaration) order.</summary>
	public int ProgramIndex { get; }

	/// <summary>Position in the derived execution order (0-based).</summary>
	public int Order { get; }

	/// <summary>Program indices of the passes that must complete before this one. Always less than
	/// <see cref="ProgramIndex"/> (edges point forward), so the dependency graph is a DAG.</summary>
	public ImmutableArray<int> DependsOn { get; }

	internal ScheduledPass(GraphPass pass, int programIndex, int order, ImmutableArray<int> dependsOn)
	{
		Pass = pass;
		ProgramIndex = programIndex;
		Order = order;
		DependsOn = dependsOn;
	}
}
