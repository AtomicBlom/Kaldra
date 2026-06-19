namespace Loom.Core;

/// <summary>
/// A GPU storage buffer (not an image). A compute pass <c>.ReadWrites</c> it and a draw <c>.ReadsVertex</c> it
/// (GPU particles — §7.6). Sized in bytes, not resolution-bound.
/// </summary>
public sealed class BufferHandle : ResourceHandle, IStorageHandle
{
	internal BufferHandle(int id, string name, Format format, Domain domain, int byteSize)
		: base(id, name, format, ResourceKind.Buffer, domain, scale: 1f, count: 1, byteSize: byteSize) { }
}
