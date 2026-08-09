using System.Runtime.InteropServices;
using System.Runtime.Versioning;

using Loom.Core.Animation;
using Loom.Core.Geometry;
using Loom.Core.Hosting;

namespace Loom.Backend.WebGPU;

/// <summary>
/// Owns app-uploaded, GPU-resident geometry: one vertex buffer + one index buffer per mesh, filled once at
/// creation with an immediate <c>queue.writeBuffer</c> (meshes are long-lived, so the upload cost is one-time and
/// off the per-frame path). Meshes are stable app resources referenced from pass closures by their handle; the
/// store resolves that handle to the buffers a <c>Draw</c> binds and the index count it draws.
/// </summary>
[SupportedOSPlatform("browser")]
internal sealed class WebGpuMeshStore(WebGpuHandleAllocator handles) : IDisposable
{
	private struct StoredMesh
	{
		public int VertexBuffer;
		public int IndexBuffer;
		public int IndexCount;
		public int VertexCount; // skinned-mesh bind-pose vertex count (the pre-skin dispatch's thread count)
	}

	private readonly List<StoredMesh> _meshes = [];

	/// <summary>
	/// Resolve a <see cref="Mesh.Id"/> to its slot in the store. Ids are 1-based so <c>default(Mesh)</c> (id 0) is
	/// never a live handle: drawing an unassigned <see cref="Mesh"/> field fails here instead of silently rendering
	/// whichever mesh was uploaded first.
	/// </summary>
	private int Slot(int meshId) =>
		meshId >= 1 && meshId <= _meshes.Count
			? meshId - 1
			: throw new WebGpuException(
				$"Mesh id {meshId} was not created by this engine ({(meshId == 0 ? "0 is default(Mesh), an unassigned handle" : $"only ids 1..{_meshes.Count} exist")}). " +
				"Meshes come from IEngine.CreateMesh / CreateMesh<T> / CreateSkinnedMesh.");

	public Mesh Create(ReadOnlySpan<Vertex> vertices, ReadOnlySpan<uint> indices) =>
		Create(MemoryMarshal.AsBytes(vertices), Marshal.SizeOf<Vertex>(), indices);

	public Mesh Create(ReadOnlySpan<byte> vertexData, int vertexStride, ReadOnlySpan<uint> indices)
	{
		var vertexCount = vertexStride > 0 ? vertexData.Length / vertexStride : 0;
		var (vertexBuffer, indexBuffer) = Upload(vertexData, indices);
		_meshes.Add(new StoredMesh { VertexBuffer = vertexBuffer, IndexBuffer = indexBuffer, IndexCount = indices.Length, VertexCount = vertexCount });
		return new Mesh(_meshes.Count);
	}

	public Mesh CreateSkinned(ReadOnlySpan<SkinnedVertex> vertices, ReadOnlySpan<uint> indices)
	{
		var (vertexBuffer, indexBuffer) = Upload(MemoryMarshal.AsBytes(vertices), indices);
		_meshes.Add(new StoredMesh { VertexBuffer = vertexBuffer, IndexBuffer = indexBuffer, IndexCount = indices.Length, VertexCount = vertices.Length });
		return new Mesh(_meshes.Count);
	}

	/// <summary>The bind-pose vertex buffer (a skinned mesh's pre-skin input storage buffer).</summary>
	public int BindPoseBuffer(int meshId) => _meshes[Slot(meshId)].VertexBuffer;

	public int VertexCount(int meshId) => _meshes[Slot(meshId)].VertexCount;

	/// <summary>Record a plain indexed draw of a mesh, or of <paramref name="range"/> within it, into the frame's command stream.</summary>
	public void Draw(WebGpuCommandWriter writer, int meshId, in DrawCall.IndexRange range = default)
	{
		var mesh = _meshes[Slot(meshId)];
		writer.SetVertexBuffer(0, mesh.VertexBuffer, 0);
		writer.SetIndexBuffer(mesh.IndexBuffer, indexFormat: 1, 0); // uint32
		var (first, count, baseVertex) = Resolve(meshId, mesh.IndexCount, range);
		writer.DrawIndexed(count, 1, first, baseVertex, 0);
	}

	// A range of (0, 0, 0) means the whole mesh — the default a draw that never mentions ranges carries. Anything
	// else is checked against the index buffer here, because reading past its end is undefined behaviour no backend
	// reports: the draw either renders garbage triangles or faults, depending on the driver.
	private static (int First, int Count, int BaseVertex) Resolve(int meshId, int indexCount, in DrawCall.IndexRange range)
	{
		if (range.IsEmpty)
			return (0, indexCount, 0);

		if ((long)range.FirstIndex + range.IndexCount > indexCount)
		{
			throw new InvalidOperationException(
				$"Draw asks for indices {range.FirstIndex}..{range.FirstIndex + range.IndexCount} of mesh {meshId}, " +
				$"which holds {indexCount}. The range and the mesh must come from the same geometry.");
		}
		return (range.FirstIndex, range.IndexCount, range.BaseVertex);
	}

	/// <summary>Record an indexed draw whose per-vertex stream is <paramref name="vertexBuffer"/> (the pre-skinned buffer).</summary>
	public void DrawSkinned(WebGpuCommandWriter writer, int meshId, int vertexBuffer)
	{
		var mesh = _meshes[Slot(meshId)];
		writer.SetVertexBuffer(0, vertexBuffer, 0);
		writer.SetIndexBuffer(mesh.IndexBuffer, indexFormat: 1, 0);
		writer.DrawIndexed(mesh.IndexCount, 1, 0, 0, 0);
	}

	/// <summary>Record an instanced indexed draw: the mesh at binding 0, the per-instance stream at binding 1.</summary>
	public void DrawInstanced(WebGpuCommandWriter writer, int meshId, int instanceBuffer, int instanceOffset, int instanceCount, in DrawCall.IndexRange range = default)
	{
		var mesh = _meshes[Slot(meshId)];
		writer.SetVertexBuffer(0, mesh.VertexBuffer, 0);
		writer.SetVertexBuffer(1, instanceBuffer, instanceOffset);
		writer.SetIndexBuffer(mesh.IndexBuffer, indexFormat: 1, 0);
		var (first, count, baseVertex) = Resolve(meshId, mesh.IndexCount, range);
		writer.DrawIndexed(count, instanceCount, first, baseVertex, 0);
	}

	private (int Vertex, int Index) Upload(ReadOnlySpan<byte> vertexData, ReadOnlySpan<uint> indices)
	{
		var vertex = handles.Allocate(WebGpuHandleAllocator.Buffers);
		WebGpuInterop.CreateBuffer(vertex, vertexData.Length, WebGpuStateMapping.BufferVertex | WebGpuStateMapping.BufferStorage | WebGpuStateMapping.BufferCopyDst, "mesh-vertices");
		WebGpuInterop.WriteBuffer(vertex, 0, CopyToArray(vertexData));

		var index = handles.Allocate(WebGpuHandleAllocator.Buffers);
		var indexBytes = MemoryMarshal.AsBytes(indices);
		WebGpuInterop.CreateBuffer(index, indexBytes.Length, WebGpuStateMapping.BufferIndex | WebGpuStateMapping.BufferCopyDst, "mesh-indices");
		WebGpuInterop.WriteBuffer(index, 0, CopyToArray(indexBytes));
		return (vertex, index);
	}

	// WriteBuffer marshals a Span<byte>; the interop marshaler needs a live span for the call's duration, so a
	// short-lived copy is fine (mesh upload is one-time). The vertex buffer also carries STORAGE usage so a skinned
	// mesh's bind pose can be read as a structured buffer by the pre-skin compute pass.
	private static Span<byte> CopyToArray(ReadOnlySpan<byte> data) => data.ToArray();

	public void Dispose()
	{
		foreach (var mesh in _meshes)
		{
			handles.Release(WebGpuHandleAllocator.Buffers, mesh.VertexBuffer);
			handles.Release(WebGpuHandleAllocator.Buffers, mesh.IndexBuffer);
		}
		_meshes.Clear();
	}
}
