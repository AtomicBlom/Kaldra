namespace Loom.Core.Graph;

/// <summary>
/// An app-writable parameter buffer (a uniform/constant buffer). Created with
/// <see cref="GraphBuilder.Constants{T}"/>, filled per frame (or on change) by handle via
/// <c>IEngine.Write</c>, and bound to a technique's reflected uniform slot by a pass that <c>.Reads</c> it.
/// Sized to <c>sizeof(T)</c>; the struct written through <c>Write</c> must match the shader's uniform-block
/// layout on both backends (std140 / cbuffer packing — prefer <c>float4</c> rows).
/// </summary>
public sealed class ConstantsHandle : ResourceHandle, IUniformHandle
{
	internal ConstantsHandle(int id, string name, Domain domain, int byteSize)
		: base(id, name, Format.Rgba8Unorm, ResourceKind.Constants, domain, scale: 1f, count: 1, byteSize: byteSize) { }
}
