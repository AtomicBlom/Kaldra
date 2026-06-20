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
		public uint VertexCount; // M15: skinned-mesh bind-pose vertex count (the pre-skin dispatch's thread count)
	}

	private readonly DX12Context _ctx;
	private readonly List<StoredMesh> _meshes = [];

	public DX12MeshStore(DX12Context ctx) => _ctx = ctx;

	/// <summary>Upload canonical <see cref="Vertex"/> geometry (the <see cref="VertexLayout.MeshStandard"/> layout) and return a handle.</summary>
	public Mesh Create(ReadOnlySpan<Vertex> vertices, ReadOnlySpan<uint> indices) =>
		Create(MemoryMarshal.AsBytes(vertices), sizeof(Vertex), indices);

	/// <summary>
	/// Upload raw vertex bytes laid out at <paramref name="vertexStride"/> (M12 — arbitrary
	/// <see cref="VertexLayout"/>s). The stride rides in the vertex-buffer view (DX12 binds with a per-bind
	/// stride), so the technique's per-vertex layout must match the data the app uploaded here.
	/// </summary>
	public Mesh Create(ReadOnlySpan<byte> vertexData, int vertexStride, ReadOnlySpan<uint> indices)
	{
		var stride = (uint)vertexStride;
		var vertexBytes = (uint)vertexData.Length;
		var indexBytes = (uint)(indices.Length * sizeof(uint));

		var vertexBuffer = CreateUploadBuffer(vertexData, vertexBytes);
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

	/// <summary>
	/// Upload a skinned mesh's bind-pose vertices (M15). The UPLOAD-heap buffer is already GPU-readable in
	/// GENERIC_READ, so its GPU address binds directly as a structured-buffer root SRV for the compute pre-skin
	/// pass (<see cref="BindPoseAddress"/>) — no extra usage flag is needed (unlike Vulkan). The vertex count is
	/// recorded for the dispatch's thread count; the index buffer is what <see cref="DrawSkinned"/> draws.
	/// </summary>
	public Mesh CreateSkinned(ReadOnlySpan<SkinnedVertex> vertices, ReadOnlySpan<uint> indices)
	{
		var mesh = Create(MemoryMarshal.AsBytes(vertices), System.Runtime.CompilerServices.Unsafe.SizeOf<SkinnedVertex>(), indices);
		var stored = _meshes[mesh.Id];
		stored.VertexCount = (uint)vertices.Length;
		_meshes[mesh.Id] = stored;
		return mesh;
	}

	/// <summary>The skinned mesh's bind-pose buffer GPU address, bound as a structured-buffer root SRV for the compute pre-skin pass (M15).</summary>
	public ulong BindPoseAddress(int meshId) => _meshes[meshId].VertexBuffer.GetGPUVirtualAddress();

	/// <summary>The skinned mesh's bind-pose vertex count — the pre-skin dispatch's thread count (M15).</summary>
	public uint VertexCount(int meshId) => _meshes[meshId].VertexCount;

	/// <summary>
	/// Draw a mesh's index buffer with its per-vertex stream sourced from <paramref name="vertexView"/> instead of
	/// the mesh's own vertex buffer (M15 — the skinned draw, reading the compute pre-skin pass's MeshStandard-shaped
	/// output). The mesh contributes only its index buffer + count.
	/// </summary>
	public void DrawSkinned(ComPtr<ID3D12GraphicsCommandList> cmd, int meshId, VertexBufferView vertexView)
	{
		var mesh = _meshes[meshId];
		var ibv = mesh.IndexView;
		cmd.IASetPrimitiveTopology(D3DPrimitiveTopology.D3DPrimitiveTopologyTrianglelist);
		cmd.IASetVertexBuffers(0, 1, &vertexView);
		cmd.IASetIndexBuffer(&ibv);
		cmd.DrawIndexedInstanced(mesh.IndexCount, 1, 0, 0, 0);
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

	/// <summary>
	/// Bind a mesh at slot 0 (per-vertex) plus <paramref name="instanceView"/> at slot 1 (per-instance) and
	/// record an instanced indexed draw of <paramref name="instanceCount"/> copies (M12 — GPU instancing). The
	/// instance view points at either an engine-managed per-frame upload buffer or a graph storage buffer a
	/// compute filled.
	/// </summary>
	public void DrawInstanced(ComPtr<ID3D12GraphicsCommandList> cmd, int meshId, VertexBufferView instanceView, uint instanceCount)
	{
		var mesh = _meshes[meshId];
		var ibv = mesh.IndexView;
		var views = stackalloc VertexBufferView[2] { mesh.VertexView, instanceView };
		cmd.IASetPrimitiveTopology(D3DPrimitiveTopology.D3DPrimitiveTopologyTrianglelist);
		cmd.IASetVertexBuffers(0, 2, views);
		cmd.IASetIndexBuffer(&ibv);
		cmd.DrawIndexedInstanced(mesh.IndexCount, instanceCount, 0, 0, 0);
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
