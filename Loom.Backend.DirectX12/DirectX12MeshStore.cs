using System.Runtime.InteropServices;
using Loom.Core.Animation;
using Loom.Core.Geometry;
using Loom.Core.Hosting;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D12;
using DxgiFormat = Silk.NET.DXGI.Format;

namespace Loom.Backend.DirectX12;

/// <summary>
/// Owns GPU-resident geometry — the DirectX12 counterpart of the Vulkan backend's <c>VulkanMeshStore</c>. The app
/// builds meshes up front through the engine and gets back an opaque <see cref="Mesh"/> handle; recording
/// resolves the handle to its vertex/index buffer views and emits the indexed draw.
/// <para>
/// Each mesh's vertices + indices live in an <c>UPLOAD</c>-heap committed resource held in
/// <c>GENERIC_READ</c> — directly GPU-readable, so no staging copy or extra synchronisation is needed. For the
/// small, static, build-once demo geometry this is the simplest correct path; a device-local + copy-queue
/// upload (the Vulkan store's staging path) is a later optimisation, not a correctness need.
/// </para>
/// </summary>
internal sealed unsafe class DirectX12MeshStore : IDisposable
{
	private struct StoredMesh
	{
		public ComPtr<ID3D12Resource> VertexBuffer;
		public ComPtr<ID3D12Resource> IndexBuffer;
		public VertexBufferView VertexView;
		public IndexBufferView IndexView;
		public uint IndexCount;
		public uint VertexCount; // skinned-mesh bind-pose vertex count (the pre-skin dispatch's thread count)
		public bool Released;    // the id's slot after IEngine.ReleaseMesh: no buffers, and Slot refuses it
	}

	private readonly DirectX12Context _ctx;
	private readonly List<StoredMesh> _meshes = [];
	// Released meshes whose buffers a frame in flight may still read, each with the fence value signalled after the
	// last frame that can.
	private readonly List<(StoredMesh Mesh, ulong LastReadingFence)> _releasing = [];

	// Mesh ids are 1-based, so 0 is a value no mesh can have — "the command list's vertex/index bindings are not this
	// store's doing".
	private const int NothingBound = 0;
	private int _bound = NothingBound;

	// Built on the first indirect draw and kept; one indexed-draw layout covers every indirect draw the engine issues.
	private ComPtr<ID3D12CommandSignature> _indirectSignature;

	public DirectX12MeshStore(DirectX12Context ctx) => _ctx = ctx;

	/// <summary>
	/// Resolve a <see cref="Mesh.Id"/> to its slot in the store. Ids are 1-based so <c>default(Mesh)</c> (id 0) is
	/// never a live handle: drawing an unassigned <see cref="Mesh"/> field fails here instead of silently rendering
	/// whichever mesh was uploaded first. A released mesh keeps its slot, emptied, so its stale handle fails here too.
	/// </summary>
	private int Slot(int meshId)
	{
		if (meshId < 1 || meshId > _meshes.Count)
		{
			throw new InvalidOperationException(
				$"DirectX12: mesh id {meshId} was not created by this engine ({(meshId == 0 ? "0 is default(Mesh), an unassigned handle" : $"only ids 1..{_meshes.Count} exist")}). " +
				"Meshes come from IEngine.CreateMesh / CreateMesh<T> / CreateSkinnedMesh.");
		}
		if (_meshes[meshId - 1].Released)
			throw new InvalidOperationException($"DirectX12: mesh id {meshId} was released by IEngine.ReleaseMesh; its handle can no longer be drawn or released.");
		return meshId - 1;
	}

	/// <summary>Upload canonical <see cref="Vertex"/> geometry (the <see cref="VertexLayout.MeshStandard"/> layout) and return a handle.</summary>
	public Mesh Create(ReadOnlySpan<Vertex> vertices, ReadOnlySpan<uint> indices) =>
		Create(MemoryMarshal.AsBytes(vertices), sizeof(Vertex), indices);

	/// <summary>
	/// Upload raw vertex bytes laid out at <paramref name="vertexStride"/> (arbitrary
	/// <see cref="VertexLayout"/>s). The stride rides in the vertex-buffer view (DirectX12 binds with a per-bind
	/// stride), so the technique's per-vertex layout must match the data the app uploaded here.
	/// </summary>
	public Mesh Create(ReadOnlySpan<byte> vertexData, int vertexStride, ReadOnlySpan<uint> indices)
	{
		var stride = (uint)vertexStride;
		var vertexBytes = (uint)vertexData.Length;
		var indexBytes = (uint)(indices.Length * sizeof(uint));

		var vertexBuffer = CreateUploadBuffer(vertexData, vertexBytes);
		var indexBuffer = CreateUploadBuffer(MemoryMarshal.AsBytes(indices), indexBytes);

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
		return new Mesh(_meshes.Count);
	}

	/// <summary>
	/// Upload a skinned mesh's bind-pose vertices. The UPLOAD-heap buffer is already GPU-readable in
	/// GENERIC_READ, so its GPU address binds directly as a structured-buffer root SRV for the compute pre-skin
	/// pass (<see cref="BindPoseAddress"/>) — no extra usage flag is needed (unlike Vulkan). The vertex count is
	/// recorded for the dispatch's thread count; the index buffer is what <see cref="DrawSkinned"/> draws.
	/// </summary>
	public Mesh CreateSkinned(ReadOnlySpan<SkinnedVertex> vertices, ReadOnlySpan<uint> indices)
	{
		var mesh = Create(MemoryMarshal.AsBytes(vertices), System.Runtime.CompilerServices.Unsafe.SizeOf<SkinnedVertex>(), indices);
		var stored = _meshes[Slot(mesh.Id)];
		stored.VertexCount = (uint)vertices.Length;
		_meshes[Slot(mesh.Id)] = stored;
		return mesh;
	}

	/// <summary>The skinned mesh's bind-pose buffer GPU address, bound as a structured-buffer root SRV for the compute pre-skin pass.</summary>
	public ulong BindPoseAddress(int meshId) => _meshes[Slot(meshId)].VertexBuffer.GetGPUVirtualAddress();

	/// <summary>The skinned mesh's bind-pose vertex count — the pre-skin dispatch's thread count.</summary>
	public uint VertexCount(int meshId) => _meshes[Slot(meshId)].VertexCount;

	/// <summary>
	/// Release a mesh: its id stops resolving at once, and its buffers wait until the frame fence reaches
	/// <paramref name="lastReadingFence"/>, the value signalled after the last frame that may still draw it.
	/// </summary>
	public void Release(int meshId, ulong lastReadingFence)
	{
		var slot = Slot(meshId);
		_releasing.Add((_meshes[slot], lastReadingFence));
		_meshes[slot] = new StoredMesh { Released = true };
		if (_bound == meshId)
			_bound = NothingBound;
	}

	/// <summary>Free the buffers of every released mesh whose last reading frame the fence has passed (<paramref name="completedFence"/>).</summary>
	public void CollectReleased(ulong completedFence)
	{
		for (var i = _releasing.Count - 1; i >= 0; i--)
		{
			if (_releasing[i].LastReadingFence > completedFence)
				continue;
			Destroy(_releasing[i].Mesh);
			_releasing.RemoveAt(i);
		}
	}

	private static void Destroy(StoredMesh mesh)
	{
		if (mesh.VertexBuffer.Handle is not null)
			mesh.VertexBuffer.Dispose();
		if (mesh.IndexBuffer.Handle is not null)
			mesh.IndexBuffer.Dispose();
	}

	/// <summary>
	/// Draw a mesh's index buffer with its per-vertex stream sourced from <paramref name="vertexView"/> instead of
	/// the mesh's own vertex buffer (the skinned draw, reading the compute pre-skin pass's MeshStandard-shaped
	/// output). The mesh contributes only its index buffer + count.
	/// </summary>
	public void DrawSkinned(ComPtr<ID3D12GraphicsCommandList> cmd, int meshId, VertexBufferView vertexView)
	{
		var mesh = _meshes[Slot(meshId)];
		var ibv = mesh.IndexView;
		cmd.IASetVertexBuffers(0, 1, &vertexView);
		cmd.IASetIndexBuffer(&ibv);
		cmd.DrawIndexedInstanced(mesh.IndexCount, 1, 0, 0, 0);
		_bound = NothingBound; // slot 0 came from the caller, not the mesh
	}

	/// <summary>
	/// Bind a mesh's vertex + index buffers and record an indexed draw into the command list. Consecutive draws of the
	/// same mesh skip the binds: the views are the mesh's own, so re-binding writes what the command list already
	/// holds. <see cref="ResetBindings"/> states when that memory stops being true.
	/// </summary>
	public void Draw(ComPtr<ID3D12GraphicsCommandList> cmd, int meshId, in DrawCall.IndexRange range = default)
	{
		var mesh = _meshes[Slot(meshId)];
		if (_bound != meshId)
		{
			var vbv = mesh.VertexView;
			var ibv = mesh.IndexView;
			cmd.IASetVertexBuffers(0, 1, &vbv);
			cmd.IASetIndexBuffer(&ibv);
			_bound = meshId;
		}
		var (first, count, baseVertex) = Resolve(meshId, mesh.IndexCount, range);
		cmd.DrawIndexedInstanced(count, 1, first, baseVertex, 0);
	}

	// A range of (0, 0, 0) means the whole mesh — the default a draw that never mentions ranges carries. Anything
	// else is checked against the index buffer here, because reading past its end is undefined behaviour no backend
	// reports: the draw either renders garbage triangles or faults, depending on the driver.
	private static (uint First, uint Count, int BaseVertex) Resolve(int meshId, uint indexCount, in DrawCall.IndexRange range)
	{
		if (range.IsEmpty)
			return (0, indexCount, 0);

		if ((ulong)range.FirstIndex + (ulong)range.IndexCount > indexCount)
		{
			throw new InvalidOperationException(
				$"Draw asks for indices {range.FirstIndex}..{range.FirstIndex + range.IndexCount} of mesh {meshId}, " +
				$"which holds {indexCount}. The range and the mesh must come from the same geometry.");
		}
		return ((uint)range.FirstIndex, (uint)range.IndexCount, range.BaseVertex);
	}

	/// <summary>
	/// Bind a mesh at slot 0 (per-vertex) plus <paramref name="instanceView"/> at slot 1 (per-instance) and
	/// record an instanced indexed draw of <paramref name="instanceCount"/> copies. The instance view points at
	/// either an engine-managed per-frame upload buffer or a graph storage buffer a compute filled.
	/// </summary>
	public void DrawInstanced(ComPtr<ID3D12GraphicsCommandList> cmd, int meshId, VertexBufferView instanceView, uint instanceCount, in DrawCall.IndexRange range = default)
	{
		var mesh = _meshes[Slot(meshId)];
		var ibv = mesh.IndexView;
		var views = stackalloc VertexBufferView[2] { mesh.VertexView, instanceView };
		cmd.IASetVertexBuffers(0, 2, views);
		cmd.IASetIndexBuffer(&ibv);
		var (first, count, baseVertex) = Resolve(meshId, mesh.IndexCount, range);
		cmd.DrawIndexedInstanced(count, instanceCount, first, baseVertex, 0);
		// Never memoised: slot 1 is a per-draw instance view, so "same mesh" does not mean "same bindings" here the
		// way it does for a plain draw.
		_bound = NothingBound;
	}

	/// <summary>
	/// Bind a mesh and draw it with the counts taken from <paramref name="arguments"/> at <paramref name="offset"/> —
	/// <c>D3D12_DRAW_INDEXED_ARGUMENTS</c>: index count, instance count, first index, base vertex, first instance.
	/// The CPU never learns any of them.
	/// </summary>
	public void DrawIndirect(ComPtr<ID3D12GraphicsCommandList> cmd, int meshId, ComPtr<ID3D12Resource> arguments, ulong offset)
	{
		var mesh = _meshes[Slot(meshId)];
		var vbv = mesh.VertexView;
		var ibv = mesh.IndexView;
		cmd.IASetVertexBuffers(0, 1, &vbv);
		cmd.IASetIndexBuffer(&ibv);
		_bound = meshId;
		cmd.ExecuteIndirect(IndirectSignature(), 1, arguments, offset, default(ComPtr<ID3D12Resource>), 0);
	}

	// D3D12 has no ExecuteIndirect without a command signature describing the argument layout. One indexed-draw
	// signature covers every indirect draw the engine issues, so it is built once and kept.
	private ComPtr<ID3D12CommandSignature> IndirectSignature()
	{
		if (_indirectSignature.Handle is not null)
			return _indirectSignature;

		var argument = new IndirectArgumentDesc { Type = IndirectArgumentType.DrawIndexed };
		var desc = new CommandSignatureDesc
		{
			ByteStride = (uint)sizeof(DrawIndexedArguments),
			NumArgumentDescs = 1,
			PArgumentDescs = &argument,
			NodeMask = 0,
		};
		// A signature carrying only DRAW_INDEXED changes no root arguments, so it takes a null root signature. Silk's
		// typed overloads all want a root signature by reference, which cannot express null, so this goes through the
		// raw COM entry point.
		ID3D12CommandSignature* signature = null;
		SilkMarshal.ThrowHResult(_ctx.Device.Handle->CreateCommandSignature(
			&desc, (ID3D12RootSignature*)null, SilkMarshal.GuidPtrOf<ID3D12CommandSignature>(), (void**)&signature));
		_indirectSignature = signature;
		return _indirectSignature;
	}

	/// <summary>
	/// Forget which mesh is bound. Required whenever something other than this store touches the vertex or index
	/// bindings — a new command list, a draw list's own geometry, the procedural instance stream — because the memo is
	/// a claim about the command list's recorded state, not about this store's history.
	/// </summary>
	public void ResetBindings() => _bound = NothingBound;

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
			Destroy(mesh);
		_meshes.Clear();
		foreach (var (mesh, _) in _releasing)
			Destroy(mesh);
		_releasing.Clear();
		if (_indirectSignature.Handle is not null)
			_indirectSignature.Dispose();
	}
}
