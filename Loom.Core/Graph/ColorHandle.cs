namespace Loom.Core.Graph;

/// <summary>
/// A colour image target. Produced by <c>.Writes</c>/<c>.Blends</c>, read by <c>.Samples</c>, and (as a
/// storage image — e.g. the persistent exposure value) by <c>.ReadWrites</c>. Also the static type of the
/// <see cref="GraphBuilder.Backbuffer"/> and <see cref="GraphBuilder.Persistent"/> image handles, which are
/// colour-like (their distinct lifetime is carried by <see cref="ResourceHandle.Kind"/>).
/// </summary>
public sealed class ColorHandle : ResourceHandle, ITextureHandle, IStorageHandle
{
	internal ColorHandle(int id, string name, Format format, ResourceKind kind, Domain domain, float scale, int count)
		: base(id, name, format, kind, domain, scale, count, byteSize: 0) { }
}
