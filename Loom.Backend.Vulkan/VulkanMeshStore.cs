using System.Runtime.InteropServices;
using Loom.Core.Animation;
using Loom.Core.Geometry;
using Silk.NET.Vulkan;
using VMASharp;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace Loom.Backend.Vulkan;

/// <summary>
/// Owns GPU-resident geometry: device-local vertex + index buffers, uploaded once via a host-visible
/// staging copy. The app builds meshes up front through the engine and gets back an
/// opaque <see cref="Mesh"/> handle (a small id); recording resolves the handle to its buffers and emits the
/// indexed draw. Buffers are allocated through the shared VMA allocator. No per-frame churn — meshes are
/// long-lived resources, freed only on teardown.
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
	}

	private readonly VulkanContext _ctx;
	private readonly Vk _vk;
	private readonly VulkanMemoryAllocator _allocator;
	private readonly CommandPool _commandPool;
	private readonly List<StoredMesh> _meshes = [];

	public VulkanMeshStore(VulkanContext ctx, VulkanMemoryAllocator allocator, CommandPool commandPool)
	{
		_ctx = ctx;
		_vk = ctx.Vk;
		_allocator = allocator;
		_commandPool = commandPool;
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

		var id = _meshes.Count;
		_meshes.Add(new StoredMesh
		{
			VertexBuffer = vertexBuffer,
			VertexAllocation = vertexAllocation,
			IndexBuffer = indexBuffer,
			IndexAllocation = indexAllocation,
			IndexCount = (uint)indices.Length,
		});
		return new Mesh(id, indices.Length);
	}

	/// <summary>
	/// Upload a skinned mesh's bind-pose vertices — like the byte-stride <see cref="Create(ReadOnlySpan{byte}, int, ReadOnlySpan{uint})"/>,
	/// but the vertex buffer also carries <see cref="BufferUsageFlags.StorageBufferBit"/> so the compute pre-skin
	/// pass can read it as a structured buffer (<see cref="BindPoseBuffer"/>). The vertex count is recorded for the
	/// dispatch's thread count.
	/// </summary>
	public Mesh CreateSkinned(ReadOnlySpan<SkinnedVertex> vertices, ReadOnlySpan<uint> indices)
	{
		var vertexData = MemoryMarshal.AsBytes(vertices);
		var vertexBuffer = Upload(vertexData, BufferUsageFlags.VertexBufferBit | BufferUsageFlags.StorageBufferBit, out var vertexAllocation);
		var indexBuffer = Upload(MemoryMarshal.AsBytes(indices), BufferUsageFlags.IndexBufferBit, out var indexAllocation);

		var id = _meshes.Count;
		_meshes.Add(new StoredMesh
		{
			VertexBuffer = vertexBuffer,
			VertexAllocation = vertexAllocation,
			IndexBuffer = indexBuffer,
			IndexAllocation = indexAllocation,
			IndexCount = (uint)indices.Length,
			VertexCount = (uint)vertices.Length,
		});
		return new Mesh(id, indices.Length);
	}

	/// <summary>The skinned mesh's bind-pose vertex buffer, for the compute pre-skin pass to read as a structured buffer.</summary>
	public Buffer BindPoseBuffer(int meshId) => _meshes[meshId].VertexBuffer;

	/// <summary>The skinned mesh's bind-pose vertex count — the pre-skin dispatch's thread count.</summary>
	public uint VertexCount(int meshId) => _meshes[meshId].VertexCount;

	/// <summary>
	/// Draw a mesh's index buffer with its per-vertex stream sourced from <paramref name="vertexBuffer"/> instead
	/// of the mesh's own vertex buffer (the skinned draw: the compute pre-skin pass wrote a MeshStandard-shaped
	/// output buffer). The mesh contributes only its index buffer + count.
	/// </summary>
	public void DrawSkinned(CommandBuffer cmd, int meshId, Buffer vertexBuffer)
	{
		var mesh = _meshes[meshId];
		var offset = 0ul;
		_vk.CmdBindVertexBuffers(cmd, 0, 1, in vertexBuffer, in offset);
		_vk.CmdBindIndexBuffer(cmd, mesh.IndexBuffer, 0, IndexType.Uint32);
		_vk.CmdDrawIndexed(cmd, mesh.IndexCount, 1, 0, 0, 0);
	}

	/// <summary>Bind a mesh's buffers and record an indexed draw into the command buffer.</summary>
	public void Draw(CommandBuffer cmd, int meshId)
	{
		var mesh = _meshes[meshId];
		var offset = 0ul;
		_vk.CmdBindVertexBuffers(cmd, 0, 1, in mesh.VertexBuffer, in offset);
		_vk.CmdBindIndexBuffer(cmd, mesh.IndexBuffer, 0, IndexType.Uint32);
		_vk.CmdDrawIndexed(cmd, mesh.IndexCount, 1, 0, 0, 0);
	}

	/// <summary>
	/// Bind a mesh at binding 0 (per-vertex) plus <paramref name="instanceBuffer"/> at binding 1 (per-instance,
	/// starting at <paramref name="instanceOffset"/> bytes) and record an instanced indexed draw of
	/// <paramref name="instanceCount"/> copies (GPU instancing). The instance buffer is either an
	/// engine-managed per-frame upload buffer (CPU-driven) or a graph storage buffer a compute filled.
	/// </summary>
	public void DrawInstanced(CommandBuffer cmd, int meshId, Buffer instanceBuffer, ulong instanceOffset, uint instanceCount)
	{
		var mesh = _meshes[meshId];
		var buffers = stackalloc Buffer[2] { mesh.VertexBuffer, instanceBuffer };
		var offsets = stackalloc ulong[2] { 0ul, instanceOffset };
		_vk.CmdBindVertexBuffers(cmd, 0, 2, buffers, offsets);
		_vk.CmdBindIndexBuffer(cmd, mesh.IndexBuffer, 0, IndexType.Uint32);
		_vk.CmdDrawIndexed(cmd, mesh.IndexCount, instanceCount, 0, 0, 0);
	}

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

		_vk.DestroyBuffer(_ctx.Device, staging, null);
		stagingAllocation.Dispose();
		return device;
	}

	// One-time blocking copy on the graphics queue — fine for up-front uploads.
	private void CopyBuffer(Buffer source, Buffer destination, ulong size)
	{
		CommandBufferAllocateInfo allocInfo = new()
		{
			SType = StructureType.CommandBufferAllocateInfo,
			CommandPool = _commandPool,
			Level = CommandBufferLevel.Primary,
			CommandBufferCount = 1,
		};
		_vk.AllocateCommandBuffers(_ctx.Device, in allocInfo, out var cmd).Check("vkAllocateCommandBuffers");

		CommandBufferBeginInfo begin = new()
		{
			SType = StructureType.CommandBufferBeginInfo,
			Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
		};
		_vk.BeginCommandBuffer(cmd, in begin).Check("vkBeginCommandBuffer");
		BufferCopy region = new() { Size = size };
		_vk.CmdCopyBuffer(cmd, source, destination, 1, in region);
		_vk.EndCommandBuffer(cmd).Check("vkEndCommandBuffer");

		SubmitInfo submit = new()
		{
			SType = StructureType.SubmitInfo,
			CommandBufferCount = 1,
			PCommandBuffers = &cmd,
		};
		_vk.QueueSubmit(_ctx.GraphicsQueue, 1, in submit, default).Check("vkQueueSubmit");
		_vk.QueueWaitIdle(_ctx.GraphicsQueue);
		_vk.FreeCommandBuffers(_ctx.Device, _commandPool, 1, in cmd);
	}

	public void Dispose()
	{
		foreach (var mesh in _meshes)
		{
			_vk.DestroyBuffer(_ctx.Device, mesh.VertexBuffer, null);
			mesh.VertexAllocation.Dispose();
			_vk.DestroyBuffer(_ctx.Device, mesh.IndexBuffer, null);
			mesh.IndexAllocation.Dispose();
		}
		_meshes.Clear();
	}
}
