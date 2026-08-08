using Loom.Core.Geometry;

namespace Loom.Core.Graph;

/// <summary>
/// A GPU storage buffer (not an image). A compute pass <c>.ReadWrites</c> it and a draw <c>.ReadsVertex</c> it
/// (GPU particles). Sized in bytes, not resolution-bound.
/// <para>
/// Declaring it in terms of what it holds — <see cref="GraphBuilder.Buffer{T}"/> or
/// <see cref="GraphBuilder.Buffer(string,int,VertexLayout)"/> — carries the element
/// <see cref="ResourceHandle.Stride"/> and <see cref="ResourceHandle.ElementCount"/> onto the handle, which is what
/// <see cref="CheckInstanceStride"/> and the instance-count guard in <c>DrawCall.Gpu</c> check the draw against.
/// The byte-size overload declares no such contract and neither guard fires.
/// </para>
/// </summary>
public sealed class BufferHandle : ResourceHandle, IStorageHandle
{
	internal BufferHandle(int id, string name, Format format, Domain domain, int byteSize, int stride = 0, int elementCount = 0)
		: base(id, name, format, ResourceKind.Buffer, domain, scale: 1f, count: 1, byteSize: byteSize, stride: stride, elementCount: elementCount) { }

	/// <summary>
	/// Verify that a technique's per-instance stream advances by the same number of bytes this buffer's elements
	/// occupy, when the buffer declared an element stride. A mismatch means the input assembler walks the buffer at
	/// the wrong step: it reads garbage for most instances and off the end for the last ones — visible as a garbled
	/// instance field rather than as an error, on every backend, which is why this is checked rather than trusted.
	/// <para>
	/// Both strides must be known for the check to mean anything: a <paramref name="techniqueStride"/> of 0 means the
	/// technique declares no per-instance layout, and a <see cref="ResourceHandle.Stride"/> of 0 means the buffer was
	/// declared as an opaque byte size. Either way there is no contract to compare, and the call is a no-op.
	/// </para>
	/// </summary>
	/// <param name="techniqueStride">The stride of the technique's per-instance <see cref="VertexLayout"/>, or 0 if it declares none.</param>
	/// <param name="technique">The technique's name, for the exception message.</param>
	public void CheckInstanceStride(int techniqueStride, string technique)
	{
		if (Stride == 0 || techniqueStride == 0 || Stride == techniqueStride)
			return;

		throw new InvalidOperationException(
			$"Technique '{technique}' advances its per-instance stream {techniqueStride} bytes per instance, but buffer " +
			$"'{Name}' was declared with {Stride}-byte elements. Declare the buffer with the same VertexLayout the " +
			"technique names as its Instance stream, so the two cannot drift.");
	}
}
