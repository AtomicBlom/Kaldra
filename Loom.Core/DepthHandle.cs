namespace Loom.Core;

/// <summary>A depth/stencil target. Written by <c>.WritesDepth</c>, tested by <c>.ReadsDepth</c>, and sampleable.</summary>
public sealed class DepthHandle : ResourceHandle, ITextureHandle
{
	internal DepthHandle(int id, string name, Format format, Domain domain, float scale)
		: base(id, name, format, ResourceKind.Depth, domain, scale, count: 1, byteSize: 0) { }
}
