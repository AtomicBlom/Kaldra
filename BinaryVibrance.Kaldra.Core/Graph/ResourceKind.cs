namespace BinaryVibrance.Kaldra.Core.Graph;

/// <summary>
/// What a declared resource fundamentally is. This is a property of the <em>declaration</em>, not of
/// how passes use it — storage-image / sampled / attachment <em>usage</em> is derived from the access
/// edges ("derived, not declared"), never stated here.
/// </summary>
public enum ResourceKind
{
	/// <summary>A colour target living in a resolution domain. Transient: its memory may be aliased.</summary>
	Color,

	/// <summary>A depth/stencil target. Transient.</summary>
	Depth,

	/// <summary>
	/// A GPU storage buffer (not an image) — a compute pass writes it, a draw reads it as vertex data
	/// (GPU particles). Sized in bytes (<see cref="ResourceHandle.ByteSize"/>), not a domain. Transient.
	/// </summary>
	Buffer,

	/// <summary>
	/// A resource whose lifetime is forever — the transient aliaser never recycles its memory.
	/// Used for cross-frame history (e.g. auto-exposure). Opt-out of aliasing.
	/// </summary>
	Persistent,

	/// <summary>
	/// An app-writable parameter buffer — a small, host-visible block a technique reads as a uniform/constant
	/// buffer. The app fills it by handle (<c>IEngine.Write</c>); a pass that <c>.Reads</c> it binds it to the
	/// technique's reflected uniform slot. Backed by one host-visible buffer per frame in flight (so a CPU write
	/// never races the GPU read of a frame still in flight), never aliased, and needs no barrier (host-coherent,
	/// CPU-written then shader-read). Sized in bytes (<see cref="ResourceHandle.ByteSize"/>).
	/// </summary>
	Constants,

	/// <summary>
	/// An app-writable storage buffer — a host-visible array a technique reads as a read-only structured buffer
	/// (an SSBO / <c>StructuredBuffer&lt;T&gt;</c>), for data too large or too variably-indexed for a uniform
	/// block. The app fills it by handle (<c>IEngine.Write</c> with a span); a pass that <c>.Reads</c> it binds it
	/// to the technique's reflected storage-buffer slot. Like <see cref="Constants"/> it is backed by one
	/// host-visible buffer per frame in flight, never aliased, and needs no barrier (host-coherent, CPU-written
	/// then shader-read). Sized in bytes (<see cref="ResourceHandle.ByteSize"/> = element count × stride).
	/// </summary>
	Storage,

	/// <summary>
	/// The swapchain colour target. Lives in the backbuffer domain at native resolution; its concrete
	/// format is chosen by the backend (the surface format), not by the author.
	/// </summary>
	Backbuffer,
}
