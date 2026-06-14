using System.Runtime.InteropServices;

using Loom.Core;

using Silk.NET.Core.Native;
using Silk.NET.Direct3D12;

using DxgiFormat = Silk.NET.DXGI.Format;

namespace Loom.DX12;

/// <summary>
/// Owns GPU-resident geometry — the DX12 counterpart of the Vulkan backend's <see cref="MeshStore"/>. The app
/// builds meshes up front through the engine and gets back an opaque <see cref="Mesh"/> handle; recording
/// resolves the handle to its vertex/index buffer views and emits the indexed draw.
/// <para>
/// Each mesh's vertices + indices live in an <c>UPLOAD</c>-heap committed resource held in
/// <c>GENERIC_READ</c> — directly GPU-readable, so no staging copy or extra synchronisation is needed. For the
/// small, static, build-once demo geometry this is the simplest correct path; a device-local + copy-queue
/// upload (the Vulkan store's staging path) is a later optimisation, not a correctness need.
/// </para>
/// </summary>
internal sealed unsafe class DX12MeshStore : IDisposable
{
	private struct StoredMesh
	{
		public ComPtr<ID3D12Resource> VertexBuffer;
		public ComPtr<ID3D12Resource> IndexBuffer;
		public VertexBufferView VertexView;
		public IndexBufferView IndexView;
		public uint IndexCount;
	}

	private readonly DX12Context _ctx;
	private readonly List<StoredMesh> _meshes = [];

	public DX12MeshStore(DX12Context ctx) => _ctx = ctx;

	/// <summary>Upload geometry and return a handle the graph's <c>ctx.Draw</c> can reference.</summary>
	public Mesh Create(ReadOnlySpan<Vertex> vertices, ReadOnlySpan<uint> indices)
	{
		var stride = (uint)sizeof(Vertex);
		var vertexBytes = (uint)(vertices.Length * sizeof(Vertex));
		var indexBytes = (uint)(indices.Length * sizeof(uint));

		var vertexBuffer = CreateUploadBuffer(MemoryMarshal.AsBytes(vertices), vertexBytes);
		var indexBuffer = CreateUploadBuffer(MemoryMarshal.AsBytes(indices), indexBytes);

		var id = _meshes.Count;
		_meshes.Add(new StoredMesh
		{
			VertexBuffer = vertexBuffer,
			IndexBuffer = indexBuffer,
			VertexView = new VertexBufferView
			{
				BufferLocation = vertexBuffer.GetGPUVirtualAddress(),
				SizeInBytes = vertexBytes,
				StrideInBytes = stride,
			},
			IndexView = new IndexBufferView
			{
				BufferLocation = indexBuffer.GetGPUVirtualAddress(),
				SizeInBytes = indexBytes,
				Format = DxgiFormat.FormatR32Uint,
			},
			IndexCount = (uint)indices.Length,
		});
		return new Mesh(id, indices.Length);
	}

	/// <summary>Bind a mesh's vertex + index buffers and record an indexed draw into the command list.</summary>
	public void Draw(ComPtr<ID3D12GraphicsCommandList> cmd, int meshId)
	{
		var mesh = _meshes[meshId];
		var vbv = mesh.VertexView;
		var ibv = mesh.IndexView;
		cmd.IASetPrimitiveTopology(D3DPrimitiveTopology.D3DPrimitiveTopologyTrianglelist);
		cmd.IASetVertexBuffers(0, 1, &vbv);
		cmd.IASetIndexBuffer(&ibv);
		cmd.DrawIndexedInstanced(mesh.IndexCount, 1, 0, 0, 0);
	}

	// An UPLOAD-heap committed buffer holding the data, left in GENERIC_READ so the GPU can read it directly
	// as a vertex/index buffer without a transition (upload-heap resources are created in GENERIC_READ).
	private ComPtr<ID3D12Resource> CreateUploadBuffer(ReadOnlySpan<byte> data, uint size)
	{
		var heapProps = new HeapProperties
		{
			Type = HeapType.Upload,
			CPUPageProperty = CpuPageProperty.Unknown,
			MemoryPoolPreference = MemoryPool.Unknown,
			CreationNodeMask = 1,
			VisibleNodeMask = 1,
		};
		var desc = new ResourceDesc
		{
			Dimension = ResourceDimension.Buffer,
			Alignment = 0,
			Width = size,
			Height = 1,
			DepthOrArraySize = 1,
			MipLevels = 1,
			Format = DxgiFormat.FormatUnknown,
			SampleDesc = new Silk.NET.DXGI.SampleDesc { Count = 1, Quality = 0 },
			Layout = TextureLayout.LayoutRowMajor,
			Flags = ResourceFlags.None,
		};

		ComPtr<ID3D12Resource> buffer = default;
		SilkMarshal.ThrowHResult(_ctx.Device.CreateCommittedResource(
			&heapProps, HeapFlags.None, &desc, ResourceStates.GenericRead, (ClearValue*)null, out buffer));

		// Map (a buffer has one subresource, no read range), copy the geometry in, unmap.
		void* mapped;
		var noRead = new Silk.NET.Direct3D12.Range { Begin = 0, End = 0 };
		SilkMarshal.ThrowHResult(buffer.Map(0, &noRead, &mapped));
		data.CopyTo(new Span<byte>(mapped, (int)size));
		buffer.Unmap(0, (Silk.NET.Direct3D12.Range*)null);
		return buffer;
	}

	public void Dispose()
	{
		foreach (var mesh in _meshes)
		{
			if (mesh.VertexBuffer.Handle is not null)
				mesh.VertexBuffer.Dispose();
			if (mesh.IndexBuffer.Handle is not null)
				mesh.IndexBuffer.Dispose();
		}
		_meshes.Clear();
	}
}
