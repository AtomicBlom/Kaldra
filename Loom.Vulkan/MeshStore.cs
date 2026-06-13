using System.Runtime.InteropServices;

using Loom.Core;
using Silk.NET.Vulkan;
using VMASharp;

using Buffer = Silk.NET.Vulkan.Buffer;

namespace Loom.Vulkan;

/// <summary>
/// Owns GPU-resident geometry: device-local vertex + index buffers, uploaded once via a host-visible
/// staging copy (Planning.md §12 M5). The app builds meshes up front through the engine and gets back an
/// opaque <see cref="Mesh"/> handle (a small id); recording resolves the handle to its buffers and emits the
/// indexed draw. Buffers are allocated through the shared VMA allocator. No per-frame churn — meshes are
/// long-lived resources, freed only on teardown.
/// </summary>
internal sealed unsafe class MeshStore : IDisposable
{
	private struct StoredMesh
	{
		public Buffer VertexBuffer;
		public Allocation VertexAllocation;
		public Buffer IndexBuffer;
		public Allocation IndexAllocation;
		public uint IndexCount;
	}

	private readonly VulkanContext _ctx;
	private readonly Vk _vk;
	private readonly VulkanMemoryAllocator _allocator;
	private readonly CommandPool _commandPool;
	private readonly List<StoredMesh> _meshes = [];

	public MeshStore(VulkanContext ctx, VulkanMemoryAllocator allocator, CommandPool commandPool)
	{
		_ctx = ctx;
		_vk = ctx.Vk;
		_allocator = allocator;
		_commandPool = commandPool;
	}

	/// <summary>Upload geometry and return a handle the graph's <c>ctx.Draw</c> can reference.</summary>
	public Mesh Create(ReadOnlySpan<Vertex> vertices, ReadOnlySpan<uint> indices)
	{
		var vertexBuffer = Upload(MemoryMarshal.AsBytes(vertices), BufferUsageFlags.VertexBufferBit, out var vertexAllocation);
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

	/// <summary>Bind a mesh's buffers and record an indexed draw into the command buffer.</summary>
	public void Draw(CommandBuffer cmd, int meshId)
	{
		var mesh = _meshes[meshId];
		var offset = 0ul;
		_vk.CmdBindVertexBuffers(cmd, 0, 1, in mesh.VertexBuffer, in offset);
		_vk.CmdBindIndexBuffer(cmd, mesh.IndexBuffer, 0, IndexType.Uint32);
		_vk.CmdDrawIndexed(cmd, mesh.IndexCount, 1, 0, 0, 0);
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
