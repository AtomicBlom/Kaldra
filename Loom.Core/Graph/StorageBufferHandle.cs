namespace Loom.Core.Graph;

/// <summary>
/// An app-writable storage buffer (a read-only SSBO / <c>StructuredBuffer&lt;T&gt;</c>). Created with
/// <see cref="GraphBuilder.Storage{T}"/>, filled per frame (or on change) by handle via <c>IEngine.Write</c> with a
/// span, and bound to a technique's reflected storage-buffer slot by a pass that <c>.Reads</c> it. The bigger
/// sibling of <see cref="ConstantsHandle"/>: an array, for data too large or too dynamically-indexed for a uniform
/// block. Sized to <c>count × sizeof(T)</c>; the element type written through <c>Write</c> must match the shader's
/// structured-buffer element layout. When <see cref="ResourceHandle.Growable"/> the <c>count</c> is an initial
/// capacity and a larger <c>Write</c> grows the allocation.
/// </summary>
public sealed class StorageBufferHandle : ResourceHandle, IStorageBufferHandle
{
	internal StorageBufferHandle(int id, string name, Domain domain, int byteSize, bool growable = false)
		: base(id, name, Format.Rgba8Unorm, ResourceKind.Storage, domain, scale: 1f, count: 1, byteSize: byteSize, growable: growable) { }
}
