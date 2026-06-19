using System.Collections.Immutable;
using Loom.Core;

namespace Loom.Graph;

/// <summary>A set of transients that may share one physical allocation: no two are ever live at once.</summary>
/// <param name="Index">The bucket's index (also the order it was opened).</param>
/// <param name="Members">The resources assigned to this bucket, in lifetime order.</param>
public readonly record struct AliasBucket(int Index, ImmutableArray<ResourceHandle> Members);