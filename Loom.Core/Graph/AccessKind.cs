namespace Loom.Core.Graph;

/// <summary>
/// How a single pass touches a single resource. These edges are the <em>entire</em> input to the
/// Execute layer: ordering, barriers, layout transitions and aliasing are all derived from them
/// The <see cref="Write"/> vs <see cref="Blend"/> distinction is load-bearing —
/// it tells the barrier-deriver whether the contents are produced (clearable) or accumulated onto.
/// </summary>
public enum AccessKind
{
	/// <summary>Colour output. The pass produces this; the engine may clear it. (<c>.Writes</c>)</summary>
	Write,

	/// <summary>
	/// Colour read-modify-write: the existing contents are loaded (preserved), then the technique's blend
	/// accumulates onto them. The pass is simultaneously the last reader and the next writer. (<c>.Blends</c>)
	/// </summary>
	Blend,

	/// <summary>Sampled (shader-read) input. (<c>.Samples</c>)</summary>
	Sample,

	/// <summary>Depth write. (<c>.WritesDepth</c>)</summary>
	WriteDepth,

	/// <summary>Depth test, no write. (<c>.ReadsDepth</c>)</summary>
	ReadDepth,

	/// <summary>General read-modify-write — read-write storage resources (images/buffers), used by compute. (<c>.ReadWrites</c>)</summary>
	ReadWrite,

	/// <summary>A buffer read as vertex-attribute data by a draw (GPU particles). (<c>.ReadsVertex</c>)</summary>
	ReadVertex,

	/// <summary>
	/// A buffer read as draw arguments by the command processor — index count, instance count, offsets — rather than
	/// by any shader stage. (<c>.ReadsIndirect</c>.) The edge is what lets a compute pass fill the arguments and a
	/// draw consume them in the same frame with the hand-off derived, exactly as <see cref="ReadVertex"/> does for a
	/// compute-filled instance stream.
	/// </summary>
	ReadIndirect,

	/// <summary>
	/// An app-writable parameter buffer read as a shader uniform/constant block. (<c>.Reads</c> of a
	/// <see cref="ConstantsHandle"/>.) Host-written and host-coherent, so it derives no barrier — the edge only
	/// binds the buffer to the technique's uniform slot and keeps the resource alive.
	/// </summary>
	ReadUniform,

	/// <summary>
	/// An app-writable storage buffer read as a read-only structured buffer (SSBO). (<c>.Reads</c> of a
	/// <see cref="StorageBufferHandle"/>.) Host-written and host-coherent, so it derives no barrier — the edge
	/// only binds the buffer to the technique's storage-buffer slot and keeps the resource alive.
	/// </summary>
	ReadStorage,
}
