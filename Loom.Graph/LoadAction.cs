namespace Loom.Graph;

/// <summary>
/// What happens to an attachment's existing contents when a pass begins rendering to it. With dynamic
/// rendering this is the <c>VkRenderingAttachmentInfo.loadOp</c>; derivation picks it from the access and
/// the resource's history (first write = clear, write-over-existing or blend = load).
/// </summary>
public enum LoadAction
{
	/// <summary>Clear to a default before rendering. First <c>.Writes</c> of a fresh transient.</summary>
	Clear,

	/// <summary>Preserve existing contents. <c>.Blends</c>, or a <c>.Writes</c> over an already-populated target.</summary>
	Load,

	/// <summary>Contents are fully overwritten; the previous values are irrelevant.</summary>
	DontCare,
}