namespace BinaryVibrance.Kaldra.Core.Graph;

/// <summary>
/// Per-pass budgets the Execute layer enforces, so a graph that derives on one backend derives on all of them. They
/// live here rather than in a backend because the check belongs at derive time — the backends collect these binding
/// edges identically, and each one previously clamped the overflow away in silence.
/// <para>
/// A neutral floor, not a device query. Real devices allow far more; these are the sizes the backends' descriptor
/// layouts are built for, and holding every backend to the same number is what keeps a graph portable.
/// </para>
/// </summary>
public static class GraphLimits
{
	/// <summary>
	/// Constants buffers one pass may <c>.Reads</c>. Each occupies a uniform slot in the technique's descriptor
	/// layout, and the slots are assigned in declaration order.
	/// </summary>
	public const int MaxUniformBuffersPerPass = 8;

	/// <summary>Storage buffers one pass may <c>.Reads</c>, bound to the technique's storage-buffer slots in declaration order.</summary>
	public const int MaxStorageBuffersPerPass = 8;

	/// <summary>
	/// Colour attachments one raster pass may write. Eight is the portable number, not an arbitrary one: it is both
	/// <c>D3D12_SIMULTANEOUS_RENDER_TARGET_COUNT</c> and WebGPU's <c>maxColorAttachments</c>. Vulkan's own limit is
	/// usually higher, so a pass declaring more used to derive and record there and fail on the other two —
	/// <see cref="BinaryVibrance.Kaldra.Core.Hosting.EngineCapabilities.MaxColorAttachments"/> reports whatever headroom a particular
	/// device has above this, but the graph is held to the floor so it stays portable.
	/// </summary>
	public const int MaxColorAttachmentsPerPass = 8;
}
