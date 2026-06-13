using System.Collections.Immutable;

using Loom.Core;

namespace Loom.Graph;

/// <summary>
/// The derived synchronisation work for a single pass: the layout/hazard transitions to emit <em>before</em>
/// recording it, and the load/store actions for its render targets. Backend-neutral — the Vulkan backend
/// turns <see cref="Transitions"/> into <c>vkCmdPipelineBarrier2</c> and <see cref="Attachments"/> into the
/// dynamic-rendering attachment infos.
/// </summary>
public sealed class PassBarriers
{
	/// <summary>The pass this synchronisation precedes.</summary>
	public GraphPass Pass { get; }

	/// <summary>Position in execution order (matches the source <see cref="ScheduledPass.Order"/>).</summary>
	public int Order { get; }

	/// <summary>Transitions to emit as barriers immediately before the pass records.</summary>
	public ImmutableArray<ResourceTransition> Transitions { get; }

	/// <summary>Load/store decisions for the pass's colour and depth attachments (raster passes only).</summary>
	public ImmutableArray<AttachmentAction> Attachments { get; }

	internal PassBarriers(GraphPass pass, int order, ImmutableArray<ResourceTransition> transitions, ImmutableArray<AttachmentAction> attachments)
	{
		Pass = pass;
		Order = order;
		Transitions = transitions;
		Attachments = attachments;
	}
}
