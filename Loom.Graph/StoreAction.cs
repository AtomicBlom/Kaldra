namespace Loom.Graph;

/// <summary>
/// What happens to an attachment's contents when a pass finishes (the <c>VkRenderingAttachmentInfo.storeOp</c>).
/// </summary>
public enum StoreAction
{
	/// <summary>Keep the results — the resource is read by a later pass or presented.</summary>
	Store,

	/// <summary>Discard — nothing reads this resource after the pass.</summary>
	DontCare,
}
