using System.Collections.Immutable;
using BinaryVibrance.Kaldra.Core.Graph;

namespace BinaryVibrance.Kaldra.Graph;

/// <summary>
/// The derived synchronisation work for a single pass: the layout/hazard transitions to emit <em>before</em>
/// recording it, and the load/store actions for its render targets. Backend-neutral — the Vulkan backend
/// turns <see cref="Transitions"/> into <c>vkCmdPipelineBarrier2</c> and <see cref="Attachments"/> into the
/// dynamic-rendering attachment infos.
/// <para>
/// It also carries the derived GPU debug-label nesting (<see cref="DebugLabelScopes"/>): before recording,
/// the backend closes <see cref="LabelCloses"/> open groups, opens each of <see cref="LabelOpens"/>, then
/// opens the pass's own <see cref="LeafLabel"/> scope (closed after the pass). Diagnostic-only.
/// </para>
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

	/// <summary>Open debug-label groups to close before this pass (derived from the dotted pass name; see <see cref="DebugLabelScopes"/>).</summary>
	public int LabelCloses { get; }

	/// <summary>Debug-label groups to open before this pass, outermost first.</summary>
	public ImmutableArray<string> LabelOpens { get; }

	/// <summary>The pass's own debug-label scope (the last dotted segment of its name).</summary>
	public string LeafLabel { get; }

	internal PassBarriers(GraphPass pass, int order, ImmutableArray<ResourceTransition> transitions, ImmutableArray<AttachmentAction> attachments, int labelCloses, ImmutableArray<string> labelOpens, string leafLabel)
	{
		Pass = pass;
		Order = order;
		Transitions = transitions;
		Attachments = attachments;
		LabelCloses = labelCloses;
		LabelOpens = labelOpens;
		LeafLabel = leafLabel;
	}
}
