using Loom.Core;

namespace Loom.Graph;

/// <summary>
/// A derived state change for one resource, to be realised as a <c>vkCmdPipelineBarrier2</c> image memory
/// barrier before the consuming pass. The backend turns <see cref="From"/> into the barrier's old layout
/// + source stage/access scope and <see cref="To"/> into the new layout + destination scope. <see cref="From"/>
/// may equal <see cref="To"/> — a same-layout barrier still orders a write-after-write or read-after-write
/// hazard (memory/execution dependency, no layout change).
/// </summary>
/// <param name="Resource">The resource transitioned.</param>
/// <param name="From">The state the resource is leaving (its prior usage, or <see cref="ResourceState.Undefined"/>).</param>
/// <param name="To">The state the consuming pass requires.</param>
public readonly record struct ResourceTransition(ResourceHandle Resource, ResourceState From, ResourceState To);
