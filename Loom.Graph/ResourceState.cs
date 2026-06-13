namespace Loom.Graph;

/// <summary>
/// The logical state a resource is in at a point in the frame — the backend-neutral stand-in for an
/// image layout plus its associated pipeline-stage/access scope. Barrier derivation reasons purely in
/// these states; the backend maps each to a concrete <c>(VkImageLayout, VkPipelineStageFlags2,
/// VkAccessFlags2)</c> triple. Keeping the mapping in the backend is what lets a future DX12 backend
/// reinterpret the same transitions (CLAUDE.md backend-isolation invariant).
/// </summary>
public enum ResourceState
{
	/// <summary>No defined contents — start of frame for a transient, or a freshly acquired swapchain image.</summary>
	Undefined,

	/// <summary>Bound as a colour target being written or blended (<c>COLOR_ATTACHMENT_OPTIMAL</c>).</summary>
	ColorAttachment,

	/// <summary>Bound as a depth target being written (<c>DEPTH_ATTACHMENT_OPTIMAL</c>).</summary>
	DepthAttachment,

	/// <summary>Depth bound read-only for testing (<c>DEPTH_READ_ONLY_OPTIMAL</c>).</summary>
	DepthReadOnly,

	/// <summary>Sampled in a shader (<c>SHADER_READ_ONLY_OPTIMAL</c>).</summary>
	ShaderRead,

	/// <summary>General read/write storage image or buffer, used by compute (<c>GENERAL</c>).</summary>
	Storage,

	/// <summary>Ready to be presented to the screen (<c>PRESENT_SRC_KHR</c>).</summary>
	Present,
}
