using System.Runtime.InteropServices;
using Loom.Core.Animation;
using Loom.Core.Geometry;
using Loom.Core.Hosting;
using Silk.NET.Vulkan;
using VMASharp;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace Loom.Backend.Vulkan;

/// <summary>
/// Owns GPU-resident geometry: device-local vertex + index buffers, uploaded once via a host-visible
/// staging copy. The app builds meshes up front through the engine and gets back an
/// opaque <see cref="Mesh"/> handle (a small id); recording resolves the handle to its buffers and emits the
/// indexed draw. Buffers are allocated through the shared VMA allocator. No per-frame churn — meshes are
/// long-lived resources, freed on teardown or, when the app releases one, once no frame in flight can read it.
/// </summary>
internal sealed unsafe class VulkanMeshStore : IDisposable
{
	private struct StoredMesh
	{
		public Buffer VertexBuffer;
		public Allocation VertexAllocation;
		public Buffer IndexBuffer;
		public Allocation IndexAllocation;
		public uint IndexCount;
		public uint VertexCount; // skinned-mesh bind-pose vertex count (the pre-skin dispatch's thread count)
		public bool Released;    // the id's slot after IEngine.ReleaseMesh: no buffers, and Slot refuses it
	}

	private readonly VulkanContext _ctx;
	private readonly Vk _vk;
	private readonly VulkanMemoryAllocator _allocator;
	private readonly VulkanUploadBatch _uploads;
	private readonly List<StoredMesh> _meshes = [];
	// Released meshes whose buffers a frame in flight (or a pending upload) may still read, each with the last frame
	// that can.
	private readonly List<(StoredMesh Mesh, ulong LastReadingFrame)> _releasing = [];

	// Mesh ids are 1-based, so 0 is a value no mesh can have — "the command buffer's vertex/index bindings are not
	// this store's doing".
	private const int NothingBound = 0;
	private int _bound = NothingBound;

	public VulkanMeshStore(VulkanContext ctx, VulkanMemoryAllocator allocator, VulkanUploadBatch uploads)
	{
		_ctx = ctx;
		_vk = ctx.Vk;
		_allocator = allocator;
		_uploads = uploads;
	}

	/// <summary>
	/// Resolve a <see cref="Mesh.Id"/> to its slot in the store. Ids are 1-based so <c>default(Mesh)</c> (id 0) is
	/// never a live handle: drawing an unassigned <see cref="Mesh"/> field fails here instead of silently rendering
	/// whichever mesh was uploaded first. A released mesh keeps its slot, emptied, so its stale handle fails here too.
	/// </summary>
	private int Slot(int meshId)
	{
		if (meshId < 1 || meshId > _meshes.Count)
		{
			throw new VulkanException(
				$"Mesh id {meshId} was not created by this engine ({(meshId == 0 ? "0 is default(Mesh), an unassigned handle" : $"only ids 1..{_meshes.Count} exist")}). " +
				"Meshes come from IEngine.CreateMesh / CreateMesh<T> / CreateSkinnedMesh.");
		}
		if (_meshes[meshId - 1].Released)
			throw new VulkanException($"Mesh id {meshId} was released by IEngine.ReleaseMesh; its handle can no longer be drawn or released.");
		return meshId - 1;
	}

	/// <summary>Upload canonical <see cref="Vertex"/> geometry (the <see cref="VertexLayout.MeshStandard"/> layout) and return a handle.</summary>
	public Mesh Create(ReadOnlySpan<Vertex> vertices, ReadOnlySpan<uint> indices) =>
		Create(MemoryMarshal.AsBytes(vertices), sizeof(Vertex), indices);

	/// <summary>
	/// Upload raw vertex bytes laid out at <paramref name="vertexStride"/> (arbitrary
	/// <see cref="VertexLayout"/>s). The per-vertex stride lives in the technique's pipeline (Vulkan binds
	/// vertex buffers without a stride), so the technique's layout must match the data the app uploaded here.
	/// </summary>
	public Mesh Create(ReadOnlySpan<byte> vertexData, int vertexStride, ReadOnlySpan<uint> indices)
	{
		_ = vertexStride; // recorded by the technique's pipeline, not at bind time on Vulkan
		var vertexBuffer = Upload(vertexData, BufferUsageFlags.VertexBufferBit, out var vertexAllocation);
		var indexBuffer = Upload(MemoryMarshal.AsBytes(indices), BufferUsageFlags.IndexBufferBit, out var indexAllocation);

		_meshes.Add(new StoredMesh
		{
			VertexBuffer = vertexBuffer,
			VertexAllocation = vertexAllocation,
			IndexBuffer = indexBuffer,
			IndexAllocation = indexAllocation,
			IndexCount = (uint)indices.Length,
		});
		return new Mesh(_meshes.Count);
	}

	/// <summary>
	/// Upload a skinned mesh's bind-pose vertices — like the byte-stride <see cref="Create(ReadOnlySpan{byte}, int, ReadOnlySpan{uint})"/>,
	/// but the vertex buffer also carries <see cref="BufferUsageFlags.StorageBufferBit"/> so the compute pre-skin
	/// pass can read it as a structured buffer (<see cref="VertexBuffer"/>). The vertex count is recorded for the
	/// dispatch's thread count.
	/// </summary>
	public Mesh CreateSkinned(ReadOnlySpan<SkinnedVertex> vertices, ReadOnlySpan<uint> indices)
	{
		var vertexData = MemoryMarshal.AsBytes(vertices);
		var vertexBuffer = Upload(vertexData, BufferUsageFlags.VertexBufferBit | BufferUsageFlags.StorageBufferBit, out var vertexAllocation);
		var indexBuffer = Upload(MemoryMarshal.AsBytes(indices), BufferUsageFlags.IndexBufferBit, out var indexAllocation);

		_meshes.Add(new StoredMesh
		{
			VertexBuffer = vertexBuffer,
			VertexAllocation = vertexAllocation,
			IndexBuffer = indexBuffer,
			IndexAllocation = indexAllocation,
			IndexCount = (uint)indices.Length,
			VertexCount = (uint)vertices.Length,
		});
		return new Mesh(_meshes.Count);
	}

	/// <summary>
	/// A stored mesh's vertex buffer: a skinned mesh's bind pose, read by the compute pre-skin pass as a structured buffer, or a
	/// static instance buffer's instances, bound as a draw's per-instance stream.
	/// </summary>
	public Buffer VertexBuffer(int meshId) => _meshes[Slot(meshId)].VertexBuffer;

	/// <summary>The skinned mesh's bind-pose vertex count — the pre-skin dispatch's thread count.</summary>
	public uint VertexCount(int meshId) => _meshes[Slot(meshId)].VertexCount;

	/// <summary>
	/// Release a mesh: its id stops resolving at once, and its buffers wait until frame
	/// <paramref name="lastReadingFrame"/> has completed — a frame already submitted may still draw it, and the upload
	/// batch that fills it rides ahead of the next frame, so the caller names that next frame. Returns the buffers, which
	/// live until then, so the caller can evict whatever is keyed by their handles before the driver can reuse them.
	/// </summary>
	public (Buffer Vertex, Buffer Index) Release(int meshId, ulong lastReadingFrame)
	{
		var slot = Slot(meshId);
		var mesh = _meshes[slot];
		_releasing.Add((mesh, lastReadingFrame));
		_meshes[slot] = new StoredMesh { Released = true };
		if (_bound == meshId)
			_bound = NothingBound;
		return (mesh.VertexBuffer, mesh.IndexBuffer);
	}

	/// <summary>Free the buffers of every released mesh whose last reading frame is <paramref name="completedFrame"/> or earlier.</summary>
	public void CollectReleased(ulong completedFrame)
	{
		for (var i = _releasing.Count - 1; i >= 0; i--)
		{
			if (_releasing[i].LastReadingFrame > completedFrame)
				continue;
			Destroy(_releasing[i].Mesh);
			_releasing.RemoveAt(i);
		}
	}

	private void Destroy(in StoredMesh mesh)
	{
		_vk.DestroyBuffer(_ctx.Device, mesh.VertexBuffer, null);
		mesh.VertexAllocation.Dispose();
		_vk.DestroyBuffer(_ctx.Device, mesh.IndexBuffer, null);
		mesh.IndexAllocation.Dispose();
	}

	/// <summary>
	/// Draw a mesh's index buffer with its per-vertex stream sourced from <paramref name="vertexBuffer"/> instead
	/// of the mesh's own vertex buffer (the skinned draw: the compute pre-skin pass wrote a MeshStandard-shaped
	/// output buffer). The mesh contributes only its index buffer + count.
	/// </summary>
	public void DrawSkinned(CommandBuffer cmd, int meshId, Buffer vertexBuffer)
	{
		var mesh = _meshes[Slot(meshId)];
		var offset = 0ul;
		_vk.CmdBindVertexBuffers(cmd, 0, 1, in vertexBuffer, in offset);
		_vk.CmdBindIndexBuffer(cmd, mesh.IndexBuffer, 0, IndexType.Uint32);
		_vk.CmdDrawIndexed(cmd, mesh.IndexCount, 1, 0, 0, 0);
		_bound = NothingBound; // binding 0 came from the caller, not the mesh
	}

	/// <summary>
	/// Bind a mesh's buffers and record an indexed draw into the command buffer. Consecutive draws of the same mesh
	/// skip the binds: the buffers are the mesh's own, so re-binding writes the handles the command buffer already
	/// holds. <see cref="ResetBindings"/> states when that memory stops being true.
	/// </summary>
	public void Draw(CommandBuffer cmd, int meshId, in DrawCall.IndexRange range = default)
	{
		var mesh = _meshes[Slot(meshId)];
		if (_bound != meshId)
		{
			var offset = 0ul;
			_vk.CmdBindVertexBuffers(cmd, 0, 1, in mesh.VertexBuffer, in offset);
			_vk.CmdBindIndexBuffer(cmd, mesh.IndexBuffer, 0, IndexType.Uint32);
			_bound = meshId;
		}
		var (first, count, baseVertex) = Resolve(meshId, mesh.IndexCount, range);
		_vk.CmdDrawIndexed(cmd, count, 1, first, baseVertex, 0);
	}

	// A range of (0, 0, 0) means the whole mesh — the default a draw that never mentions ranges carries. Anything
	// else is checked against the index buffer here, because reading past its end is undefined behaviour no backend
	// reports: the draw either renders garbage triangles or faults, depending on the driver.
	private (uint First, uint Count, int BaseVertex) Resolve(int meshId, uint indexCount, in DrawCall.IndexRange range)
	{
		if (range.IsEmpty)
			return (0, indexCount, 0);

		if ((ulong)range.FirstIndex + (ulong)range.IndexCount > indexCount)
		{
			throw new VulkanException(
				$"Draw asks for indices {range.FirstIndex}..{range.FirstIndex + range.IndexCount} of mesh {meshId}, " +
				$"which holds {indexCount}. The range and the mesh must come from the same geometry.");
		}
		return ((uint)range.FirstIndex, (uint)range.IndexCount, range.BaseVertex);
	}

	/// <summary>
	/// Bind a mesh at binding 0 (per-vertex) plus <paramref name="instanceBuffer"/> at binding 1 (per-instance,
	/// starting at <paramref name="instanceOffset"/> bytes) and record an instanced indexed draw of
	/// <paramref name="instanceCount"/> copies (GPU instancing). The instance buffer is either an
	/// engine-managed per-frame upload buffer (CPU-driven) or a graph storage buffer a compute filled.
	/// </summary>
	public void DrawInstanced(CommandBuffer cmd, int meshId, Buffer instanceBuffer, ulong instanceOffset, uint instanceCount, in DrawCall.IndexRange range = default)
	{
		var mesh = _meshes[Slot(meshId)];
		var buffers = stackalloc Buffer[2] { mesh.VertexBuffer, instanceBuffer };
		var offsets = stackalloc ulong[2] { 0ul, instanceOffset };
		_vk.CmdBindVertexBuffers(cmd, 0, 2, buffers, offsets);
		_vk.CmdBindIndexBuffer(cmd, mesh.IndexBuffer, 0, IndexType.Uint32);
		var (first, count, baseVertex) = Resolve(meshId, mesh.IndexCount, range);
		_vk.CmdDrawIndexed(cmd, count, instanceCount, first, baseVertex, 0);
		// Never memoised: binding 1 is a per-draw instance buffer at a per-draw offset, so "same mesh" does not mean
		// "same bindings" here the way it does for a plain draw.
		_bound = NothingBound;
	}

	/// <summary>
	/// Bind a mesh and draw it with the counts taken from <paramref name="arguments"/> at
	/// <paramref name="offset"/> — <c>VkDrawIndexedIndirectCommand</c>: index count, instance count, first index,
	/// vertex offset, first instance. The CPU never learns any of them.
	/// </summary>
	public void DrawIndirect(CommandBuffer cmd, int meshId, Buffer arguments, ulong offset)
	{
		var mesh = _meshes[Slot(meshId)];
		var vertexOffset = 0ul;
		_vk.CmdBindVertexBuffers(cmd, 0, 1, in mesh.VertexBuffer, in vertexOffset);
		_vk.CmdBindIndexBuffer(cmd, mesh.IndexBuffer, 0, IndexType.Uint32);
		_bound = meshId;
		_vk.CmdDrawIndexedIndirect(cmd, arguments, offset, 1, 0);
	}

	/// <summary>
	/// Forget which mesh is bound. Required whenever something other than this store touches the vertex or index
	/// bindings — a new command buffer, a draw list's own geometry, the procedural instance stream — because the
	/// memo is a claim about the command buffer's recorded state, not about this store's history.
	/// </summary>
	public void ResetBindings() => _bound = NothingBound;

	// Stage in host-visible memory, copy into a device-local buffer, free the staging buffer.
	private Buffer Upload(ReadOnlySpan<byte> data, BufferUsageFlags usage, out Allocation allocation)
	{
		var size = (ulong)data.Length;

		BufferCreateInfo stagingInfo = new()
		{
			SType = StructureType.BufferCreateInfo,
			Size = size,
			Usage = BufferUsageFlags.TransferSrcBit,
			SharingMode = SharingMode.Exclusive,
		};
		var staging = _allocator.CreateBuffer(in stagingInfo, new AllocationCreateInfo { Usage = MemoryUsage.CPU_Only }, out var stagingAllocation);
		var mapped = stagingAllocation.Map();
		data.CopyTo(new Span<byte>((void*)mapped, (int)size));
		stagingAllocation.Unmap();

		BufferCreateInfo deviceInfo = new()
		{
			SType = StructureType.BufferCreateInfo,
			Size = size,
			Usage = usage | BufferUsageFlags.TransferDstBit,
			SharingMode = SharingMode.Exclusive,
		};
		var device = _allocator.CreateBuffer(in deviceInfo, new AllocationCreateInfo { Usage = MemoryUsage.GPU_Only }, out allocation);

		CopyBuffer(staging, device, size);

		_uploads.Retire(staging, stagingAllocation);
		return device;
	}

	// Record the staging→device copy into the shared upload batch. A mesh used to cost two full GPU drains — one for
	// its vertices and one for its indices — where now both land in the batch the frame submits, alongside every
	// other resource created in the same drain.
	private void CopyBuffer(Buffer source, Buffer destination, ulong size)
	{
		var cmd = _uploads.Recorder();
		BufferCopy region = new() { Size = size };
		_vk.CmdCopyBuffer(cmd, source, destination, 1, in region);

		// Geometry is read at vertex-input, not in a shader, so its visibility barrier names that stage. Without it
		// the copy would be ordered against later submissions only by the batch's own submission order, which
		// guarantees execution start rather than write visibility.
		BufferMemoryBarrier2 barrier = new()
		{
			SType = StructureType.BufferMemoryBarrier2,
			SrcStageMask = PipelineStageFlags2.AllTransferBit,
			SrcAccessMask = AccessFlags2.TransferWriteBit,
			DstStageMask = PipelineStageFlags2.VertexInputBit | PipelineStageFlags2.ComputeShaderBit,
			DstAccessMask = AccessFlags2.VertexAttributeReadBit | AccessFlags2.IndexReadBit | AccessFlags2.ShaderStorageReadBit,
			SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
			DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
			Buffer = destination,
			Offset = 0,
			Size = Vk.WholeSize,
		};
		DependencyInfo dependency = new()
		{
			SType = StructureType.DependencyInfo,
			BufferMemoryBarrierCount = 1,
			PBufferMemoryBarriers = &barrier,
		};
		_vk.CmdPipelineBarrier2(cmd, in dependency);
	}

	public void Dispose()
	{
		foreach (var mesh in _meshes)
		{
			if (!mesh.Released)
				Destroy(mesh);
		}
		_meshes.Clear();
		foreach (var (mesh, _) in _releasing)
			Destroy(mesh);
		_releasing.Clear();
	}
}
