namespace Loom.Core.Graph;

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
}
