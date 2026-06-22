using System.Numerics;
using Loom.Core.Geometry;
using Loom.Core.Graph;
using Loom.Core.Hosting;
using Loom.Core.Materials;
using Loom.Graph;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D12;

using DxgiFormat = Silk.NET.DXGI.Format;

namespace Loom.DirectX12;

/// <summary>
/// The DirectX12 <see cref="IRenderContext"/> — the per-frame command recorder handed to each pass's
/// <c>.Draw</c> callback (the counterpart of the Vulkan backend's <c>RenderContext</c>). It is stateful: the
/// engine pushes the frame inputs + slot (<see cref="BeginFrame"/>) and, per pass, the actual render-target
/// formats + depth format (<see cref="BeginPass"/>) so a technique bakes a pipeline matching the pass's
/// attachments.
/// <para>
/// Implements the fullscreen verbs (from-scratch, sampling, and a scalar push block) and indexed mesh draw
/// (vertex/index buffers + an MVP root constant). Compute and ImGui land in following slices.
/// </para>
/// </summary>
internal sealed unsafe class DirectX12RenderContext : IRenderContext, IDisposable
{
	private const string NextSlice = "DirectX12: this render verb lands in a following executor slice.";
	private const uint DefaultShader4ComponentMapping = 0x1688; // D3D12_DEFAULT_SHADER_4_COMPONENT_MAPPING

	private readonly DirectX12Context _ctx;
	private readonly DirectX12TechniqueCache _techniques;
	private readonly DirectX12DescriptorRing _ring;
	private readonly DirectX12MeshStore _meshes;
	private readonly DirectX12TextureStore _textures;
	private readonly MaterialStore _materials;
	private readonly DirectX12InstanceUploadBuffer _instanceUpload;
	// M15-0: per-frame UPLOAD-heap ring for the uniform seam (CSM matrices, lighting params, wind), 256-aligned
	// so each block is a valid CBV base. Bound as a root CBV at b1 on a technique that reflects a constant buffer.
	private readonly DirectX12InstanceUploadBuffer _uniformUpload;
	// M15-2: per-frame UPLOAD ring for the bone palette the compute pre-skin pass reads as a structured-buffer
	// root SRV; 256-aligned (a multiple of the 64-byte matrix stride, so each block is element-aligned).
	private readonly DirectX12InstanceUploadBuffer _storageUpload;
	// Per-frame UPLOAD rings for ctx.DrawList's dynamic geometry — a vertex ring and an index ring (typeless UPLOAD
	// buffers bound by GPU address), so several draw lists in one frame append at successive offsets rather than clobber.
	private readonly DirectX12InstanceUploadBuffer _listVtx;
	private readonly DirectX12InstanceUploadBuffer _listIdx;
	private DirectX12Transients? _transients;
	private ComPtr<ID3D12GraphicsCommandList> _cmd;
	private uint _slot;
	// This frame's draw lists, pulled from FrameInputs.DrawLists and surfaced as ctx.DrawLists; a producer (the UI
	// painter) looks one up by id and submits it through DrawList.
	private IReadOnlyList<(DrawListId Id, DrawListData Data)> _drawLists = [];
	private uint _fbWidth;
	private uint _fbHeight;
	private DxgiFormat[] _colorFormats = [];
	private int _colorCount;
	private DxgiFormat _depthFormat;
	private bool _depthReadOnly;

	public float DeltaTime { get; private set; }
	public float Time { get; private set; }
	public Camera SunView { get; private set; } = Camera.Identity;
	public Camera Camera { get; private set; } = Camera.Identity;
	public IReadOnlyList<Camera> Cascades { get; private set; } = [];
	public IReadOnlyList<float> CascadeSplits { get; private set; } = [];
	public bool HdrOutput { get; private set; }
	public IReadOnlyList<(DrawListId Id, DrawListData Data)> DrawLists => _drawLists;
	private Camera[] _cascades = [];

	public DirectX12RenderContext(DirectX12Context ctx, DirectX12TechniqueCache techniques, DirectX12DescriptorRing ring, DirectX12MeshStore meshes, DirectX12TextureStore textures, MaterialStore materials, int frameCount)
	{
		_ctx = ctx;
		_techniques = techniques;
		_ring = ring;
		_meshes = meshes;
		_textures = textures;
		_materials = materials;
		_instanceUpload = new DirectX12InstanceUploadBuffer(ctx, frameCount);
		_uniformUpload = new DirectX12InstanceUploadBuffer(ctx, frameCount, alignment: 256);
		_storageUpload = new DirectX12InstanceUploadBuffer(ctx, frameCount, alignment: 256);
		_listVtx = new DirectX12InstanceUploadBuffer(ctx, frameCount);
		_listIdx = new DirectX12InstanceUploadBuffer(ctx, frameCount);
	}

	/// <summary>Bind the active graph's transient targets (the source of sampled-input SRVs). Called from SetGraph.</summary>
	public void SetTransients(DirectX12Transients transients) => _transients = transients;

	public void BeginFrame(ComPtr<ID3D12GraphicsCommandList> cmd, FrameInputs inputs, bool hdrOutput, uint slot, uint fbWidth, uint fbHeight)
	{
		_cmd = cmd;
		_slot = slot;
		_drawLists = inputs.DrawLists;
		_fbWidth = fbWidth;
		_fbHeight = fbHeight;
		DeltaTime = (float)inputs.DeltaSeconds;
		Time = inputs.Time;
		Camera = ClipSpace(inputs.Camera);
		SunView = ClipSpace(inputs.SunView);
		// Flip each cascade's clip space the same way as SunView (M15-3).
		if (_cascades.Length != inputs.Cascades.Count)
			_cascades = new Camera[inputs.Cascades.Count];
		for (var i = 0; i < inputs.Cascades.Count; i++)
			_cascades[i] = ClipSpace(inputs.Cascades[i]);
		Cascades = _cascades;
		CascadeSplits = inputs.CascadeSplits;
		HdrOutput = hdrOutput;
		_instanceUpload.BeginFrame(slot); // reset this slot's per-frame instance staging
		_uniformUpload.BeginFrame(slot);  // and its per-frame uniform staging (M15-0)
		_storageUpload.BeginFrame(slot);  // and the bone-palette staging (M15-2)
		_listVtx.BeginFrame(slot);        // and the draw-list geometry staging
		_listIdx.BeginFrame(slot);
	}

	// Adopt the Vulkan backend's clip-space convention (negate the projection's Y row), so the shared shaders need
	// NO per-backend branch — ndc.y matches the +Y-down texture V everywhere (mesh MVP, fullscreen reprojection,
	// the JFA's SV_Position). On its own this flips D3D12 upside down; the engine pairs it with a negative-height
	// viewport (DirectX12Engine.RecordRasterPass) that turns D3D12's (1-ndc.y) screen map into Vulkan's (ndc.y+1), so
	// geometry stays upright. The host supplies a neutral +Y-up projection in FrameInputs (M7-B5); both backends
	// flip it the same way here. (Earlier "identity ingest, no Y-flip" rendered mesh upright but left every
	// fullscreen pass vertically mirrored vs Vulkan — one mesh→fullscreen→backbuffer hop = upside down.)
	private static Camera ClipSpace(Camera camera) => camera.FlipClipY();

	// The parameter buffer (ResourceKind.Constants) the current pass .Reads, or -1 — bound to the technique's root CBV.
	// The app-writable parameter buffers the current pass .Reads (its ReadUniform edges, in order), bound to the
	// technique's uniform CBVs b1..bK; an in-closure push follows at b(K+1). The engine fills this in BeginPass.
	public const int MaxUniformBuffers = 8;
	private readonly int[] _activeConstantsIds = new int[MaxUniformBuffers];
	private int _activeConstantsCount;

	// The app-writable storage buffers (ResourceKind.Storage) the current pass .Reads, in order — bound to the
	// technique's read-only root SRVs (the structured-buffer SSBO seam). The engine fills this in BeginPass.
	public const int MaxStorageBuffers = 8;
	private readonly int[] _activeStorageIds = new int[MaxStorageBuffers];
	private int _activeStorageCount;

	/// <summary>
	/// Tell the context the current pass's colour-target formats + depth format (or <c>FormatUnknown</c> for no
	/// depth), so techniques bake matching pipelines. <paramref name="depthReadOnly"/> bakes with depth writes
	/// disabled (a <c>.ReadsDepth</c> pass tests against, but does not disturb, the opaque depth).
	/// <paramref name="constantsIds"/> / <paramref name="storageIds"/> are the parameter and storage buffers the
	/// pass <c>.Reads</c> (in order), bound to the technique's uniform CBVs b1.. and read-only root SRVs at draw time.
	/// </summary>
	public void BeginPass(ReadOnlySpan<DxgiFormat> colorFormats, DxgiFormat depthFormat, bool depthReadOnly, ReadOnlySpan<int> constantsIds = default, ReadOnlySpan<int> storageIds = default)
	{
		if (_colorFormats.Length < colorFormats.Length)
			_colorFormats = new DxgiFormat[colorFormats.Length];
		colorFormats.CopyTo(_colorFormats);
		_colorCount = colorFormats.Length;
		_depthFormat = depthFormat;
		_depthReadOnly = depthReadOnly;
		_activeConstantsCount = Math.Min(constantsIds.Length, MaxUniformBuffers);
		constantsIds[.._activeConstantsCount].CopyTo(_activeConstantsIds);
		_activeStorageCount = Math.Min(storageIds.Length, MaxStorageBuffers);
		storageIds[.._activeStorageCount].CopyTo(_activeStorageIds);
	}

	public void FullScreen(string technique, params ReadOnlySpan<ResourceHandle> inputs) =>
		FullScreen(technique, default, inputs);

	public void FullScreen(string technique, ReadOnlySpan<float> push, params ReadOnlySpan<ResourceHandle> inputs)
	{
		var baked = _techniques.GetOrBake(technique, _colorFormats.AsSpan(0, _colorCount), _depthFormat, _depthReadOnly);
		_cmd.SetGraphicsRootSignature(baked.RootSignature);
		_cmd.SetPipelineState(baked.Pipeline);

		// The per-pass data channel. The pass's .Reads Constants buffers bind to the technique's CBVs b1..bK; an
		// in-closure push then fills the next CBV (b(K+1)) if a reflected slot remains (M15-0 — no 16-float cap),
		// or rides the fixed 16-DWORD root-constant block when the technique declares no CBV.
		var bound = 0;
		if (baked.CbvRootParam >= 0)
		{
			for (var j = 0; j < _activeConstantsCount; j++)
				_cmd.SetGraphicsRootConstantBufferView((uint)(baked.CbvRootParam + bound++), _transients!.Resource(_slot, _activeConstantsIds[j]).GetGPUVirtualAddress());
		}
		if (push.Length > 0)
		{
			if (baked.CbvRootParam >= 0 && baked.UniformBufferCount > bound)
				_cmd.SetGraphicsRootConstantBufferView((uint)(baked.CbvRootParam + bound), _uniformUpload.Upload(_slot, System.Runtime.InteropServices.MemoryMarshal.AsBytes(push)));
			else if (baked.CbvRootParam < 0)
				SetPush(baked, push);
		}
		if (inputs.Length > 0)
			BindInputs(baked, inputs);
		// The pass's .Reads storage buffers (SSBOs) bind as read-only root SRVs from each UPLOAD buffer's address.
		if (baked.InputBufferBaseParam >= 0)
		{
			for (var s = 0; s < _activeStorageCount; s++)
				_cmd.SetGraphicsRootShaderResourceView((uint)(baked.InputBufferBaseParam + s), _transients!.Resource(_slot, _activeStorageIds[s]).GetGPUVirtualAddress());
		}

		_cmd.IASetPrimitiveTopology(D3DPrimitiveTopology.D3DPrimitiveTopologyTrianglelist);
		_cmd.DrawInstanced(3, 1, 0, 0); // fullscreen triangle, vertices fabricated from SV_VertexID
	}

	public void Draw(Mesh mesh, string technique) => Draw(mesh, technique, Camera);

	public void Draw(Mesh mesh, string technique, Camera view)
	{
		var baked = _techniques.GetOrBake(technique, _colorFormats.AsSpan(0, _colorCount), _depthFormat, _depthReadOnly);
		_cmd.SetGraphicsRootSignature(baked.RootSignature);
		_cmd.SetPipelineState(baked.Pipeline);

		// The MVP root constant (model is identity for now). The raw row-major Camera.ViewProjection is copied
		// as 16 DWORDs; the lit shader's column-major cbuffer packing transposes it back into the row-vector
		// clip = position·MVP the math expects (the same convention the Vulkan vertex push uses).
		var mvp = view.ViewProjection;
		if (baked.RootConstantParam >= 0)
			_cmd.SetGraphicsRoot32BitConstants((uint)baked.RootConstantParam, 16, &mvp, 0);

		_meshes.Draw(_cmd, mesh.Id); // binds the mesh's vertex/index buffers and records the indexed draw
	}

	public void Draw(Mesh mesh, Material material, string technique) => Draw(mesh, material, technique, Camera);

	public void Draw(Mesh mesh, Material material, string technique, Camera view)
	{
		var baked = _techniques.GetOrBake(technique, _colorFormats.AsSpan(0, _colorCount), _depthFormat, _depthReadOnly);
		_cmd.SetGraphicsRootSignature(baked.RootSignature);
		_cmd.SetPipelineState(baked.Pipeline);

		var mvp = view.ViewProjection;
		if (baked.RootConstantParam >= 0)
			_cmd.SetGraphicsRoot32BitConstants((uint)baked.RootConstantParam, 16, &mvp, 0);

		// Bind the material's textures into the technique's reflected SRV table (slots 0..N-1) — the same path
		// FullScreen uses for transients, but the SRVs come from the TextureStore (loaded textures, multi-mip).
		// A technique that samples nothing (depth-only/shadow) has no table and ignores the material.
		if (baked.TableParam >= 0 && baked.InputCount > 0)
			BindMaterialTable(baked, material, technique);

		_meshes.Draw(_cmd, mesh.Id);
	}

	// Bind a material's loaded-texture SRVs into the technique's reflected SRV table (slots 0..N-1). Shared by
	// Draw(mesh, material), textured instancing, and textured particles. The caller checks TableParam/InputCount.
	private void BindMaterialTable(DirectX12Technique baked, Material material, string technique)
	{
		var textures = _materials.TexturesOf(material.Id);
		if (textures.Length < baked.InputCount)
			throw new InvalidOperationException($"DirectX12: material supplies {textures.Length} textures but technique '{technique}' samples {baked.InputCount}.");
		var (cpu, gpu) = _ring.Allocate((uint)baked.InputCount);
		for (var i = 0; i < baked.InputCount; i++)
			WriteTextureSrv(textures[i], _ring.Offset(cpu, (uint)i));
		_cmd.SetGraphicsRootDescriptorTable((uint)baked.TableParam, gpu);
	}

	// Create an SRV over a loaded texture, exposing its full mip chain (unlike the transient WriteSrv, which is
	// single-mip). Material textures sample through this. A cube map is viewed as a TextureCube (its six array
	// slices); everything else as a Texture2D.
	private void WriteTextureSrv(int textureId, CpuDescriptorHandle dest)
	{
		var mips = _textures.MipLevels(textureId);
		var srv = new ShaderResourceViewDesc
		{
			Format = _textures.SrvFormat(textureId),
			Shader4ComponentMapping = DefaultShader4ComponentMapping,
		};
		if (_textures.IsCube(textureId))
		{
			srv.ViewDimension = SrvDimension.Texturecube;
			srv.Anonymous = new ShaderResourceViewDescUnion
			{
				TextureCube = new TexcubeSrv { MostDetailedMip = 0, MipLevels = mips, ResourceMinLODClamp = 0f },
			};
		}
		else
		{
			srv.ViewDimension = SrvDimension.Texture2D;
			srv.Anonymous = new ShaderResourceViewDescUnion
			{
				Texture2D = new Tex2DSrv { MostDetailedMip = 0, MipLevels = mips, PlaneSlice = 0, ResourceMinLODClamp = 0f },
			};
		}
		_ctx.Device.CreateShaderResourceView(_textures.Resource(textureId), &srv, dest);
	}

	// Bind a draw-list texture: a loaded texture (full mip chain) by id, or — for an inspectable graph resource (id
	// offset by DebugTextureBase, the resource viewer) — a single-mip SRV over this frame's transient image, so a
	// debug overlay can preview live graph contents through the resource's sampleable format (R32_FLOAT for depth).
	private void WriteListTextureSrv(int id, CpuDescriptorHandle dest)
	{
		if (id < InspectableResource.DebugTextureBase || _transients is null)
		{
			WriteTextureSrv(id, dest);
			return;
		}
		var rid = id - InspectableResource.DebugTextureBase;
		var srv = new ShaderResourceViewDesc
		{
			Format = _transients.SrvFormat(_slot, rid),
			Shader4ComponentMapping = DefaultShader4ComponentMapping,
			ViewDimension = SrvDimension.Texture2D,
			Anonymous = new ShaderResourceViewDescUnion
			{
				Texture2D = new Tex2DSrv { MostDetailedMip = 0, MipLevels = 1, PlaneSlice = 0, ResourceMinLODClamp = 0f },
			},
		};
		_ctx.Device.CreateShaderResourceView(_transients.Resource(_slot, rid), &srv, dest);
	}

	// Push a scalar block to the pass's root constants (the per-pass channel: JFA step, exposure rate, a
	// light-space matrix). Padded to the layout's 16 floats so the shader always reads defined memory.
	private void SetPush(DirectX12Technique baked, ReadOnlySpan<float> push)
	{
		if (baked.RootConstantParam < 0)
			throw new InvalidOperationException("DirectX12: technique has no root-constant block but a push was supplied.");
		var block = stackalloc float[16];
		for (var i = 0; i < push.Length && i < 16; i++)
			block[i] = push[i];
		_cmd.SetGraphicsRoot32BitConstants((uint)baked.RootConstantParam, 16, block, 0);
	}

	// M15-0: stage a per-frame uniform block into the uniform ring and bind it as the technique's root CBV (b1).
	// The UPLOAD-heap GPU address is bound directly — no descriptor heap entry needed for a root CBV.
	private void BindUniforms(DirectX12Technique baked, ReadOnlySpan<float> data)
	{
		var bytes = System.Runtime.InteropServices.MemoryMarshal.AsBytes(data);
		var gpuAddress = _uniformUpload.Upload(_slot, bytes);
		_cmd.SetGraphicsRootConstantBufferView((uint)baked.CbvRootParam, gpuAddress);
	}

	// Write the sampled inputs' SRVs into a fresh descriptor-ring range and bind it as the technique's SRV table.
	private void BindInputs(DirectX12Technique baked, ReadOnlySpan<ResourceHandle> inputs)
	{
		if (_transients is null)
			throw new InvalidOperationException("DirectX12: sampled inputs require an active graph (SetGraph).");
		if (baked.TableParam < 0)
			throw new InvalidOperationException("DirectX12: technique declares no SRV table but sampled inputs were supplied.");

		var (cpu, gpu) = _ring.Allocate((uint)inputs.Length);
		for (var i = 0; i < inputs.Length; i++)
		{
			var id = inputs[i].Id;
			if (!_transients.IsTransient(id))
				throw new NotSupportedException("DirectX12: sampling the backbuffer or a buffer resource lands in a later slice.");
			WriteSrv(id, _ring.Offset(cpu, (uint)i));
		}
		_cmd.SetGraphicsRootDescriptorTable((uint)baked.TableParam, gpu);
	}

	// Create a Texture2D SRV for a transient (or persistent) resource at <paramref name="dest"/>. Samples through
	// the SRV-compatible format: R32_FLOAT for a depth target (a shadow map), whose resource is TYPELESS and whose
	// DSV format (D32_FLOAT) is not SRV-creatable; == the colour format otherwise. Shared by the raster sampling
	// path and the compute-sampling path (the auto-exposure reduction reads the lit scene).
	private void WriteSrv(int id, CpuDescriptorHandle dest)
	{
		var srv = new ShaderResourceViewDesc
		{
			Format = _transients!.SrvFormat(_slot, id),
			ViewDimension = SrvDimension.Texture2D,
			Shader4ComponentMapping = DefaultShader4ComponentMapping,
			Anonymous = new ShaderResourceViewDescUnion
			{
				Texture2D = new Tex2DSrv { MostDetailedMip = 0, MipLevels = 1, PlaneSlice = 0, ResourceMinLODClamp = 0f }
			}
		};
		_ctx.Device.CreateShaderResourceView(_transients.Resource(_slot, id), &srv, dest);
	}

	public void Dispatch(string technique, (uint X, uint Y, uint Z) groups, ReadOnlySpan<float> push, ReadOnlySpan<ResourceHandle> sampled, ReadOnlySpan<ResourceHandle> storage)
	{
		if (_transients is null)
			throw new InvalidOperationException("DirectX12: compute requires an active graph (SetGraph).");

		var baked = _techniques.GetOrBake(technique, ReadOnlySpan<DxgiFormat>.Empty, DxgiFormat.FormatUnknown, false);
		_cmd.SetComputeRootSignature(baked.RootSignature);
		_cmd.SetPipelineState(baked.Pipeline);

		if (push.Length > 0 && baked.RootConstantParam >= 0)
		{
			var block = stackalloc float[16];
			for (var i = 0; i < push.Length && i < 16; i++)
				block[i] = push[i];
			_cmd.SetComputeRoot32BitConstants((uint)baked.RootConstantParam, 16, block, 0);
		}

		// Sampled inputs → an SRV descriptor table (e.g. the auto-exposure compute metering the lit scene).
		if (sampled.Length > 0 && baked.TableParam >= 0)
		{
			var (cpu, gpu) = _ring.Allocate((uint)sampled.Length);
			for (var i = 0; i < sampled.Length; i++)
				WriteSrv(sampled[i].Id, _ring.Offset(cpu, (uint)i));
			_cmd.SetComputeRootDescriptorTable((uint)baked.TableParam, gpu);
		}

		// Storage (the graph put each resource in UNORDERED_ACCESS first), bound in declaration order to match the
		// pass's .ReadWrites (as Vulkan binds them after the sampled inputs):
		//  * a RAW buffer (the FXC built-in's ByteAddressBuffer, no reflected stride) binds as a single root UAV by
		//    address — that root signature reserves one root UAV, so this path is inherently one resource.
		//  * RW STRUCTURED buffers (the particle stream) and RW textures (the 1×1 exposure image) bind as a UAV
		//    descriptor table, one descriptor per resource at u0.. — the count the root signature reserved. A
		//    structured-buffer descriptor carries StructureByteStride + NumElements so the shader's GetDimensions
		//    reports the real count (a root UAV is a raw address with no metadata → GetDimensions 0 → the particle
		//    update early-outs every thread, writes nothing).
		if (storage.Length > 0 && baked.UavRootParam >= 0)
		{
			_cmd.SetComputeRootUnorderedAccessView((uint)baked.UavRootParam, _transients.Resource(_slot, storage[0].Id).GetGPUVirtualAddress());
		}
		else if (storage.Length > 0 && baked.UavTableParam >= 0)
		{
			var (cpu, gpu) = _ring.Allocate((uint)storage.Length);
			for (var i = 0; i < storage.Length; i++)
			{
				var id = storage[i].Id;
				UnorderedAccessViewDesc uav;
				if (storage[i].Kind == ResourceKind.Buffer)
				{
					var stride = (uint)baked.StorageBufferStride;
					uav = new UnorderedAccessViewDesc
					{
						Format = DxgiFormat.FormatUnknown, // a structured buffer is format-less; the stride defines the element
						ViewDimension = UavDimension.Buffer,
						Anonymous = new UnorderedAccessViewDescUnion
						{
							Buffer = new BufferUav { FirstElement = 0, NumElements = _transients.ByteSize(id) / stride, StructureByteStride = stride, CounterOffsetInBytes = 0, Flags = BufferUavFlags.None }
						}
					};
				}
				else
				{
					uav = new UnorderedAccessViewDesc
					{
						Format = _transients.SrvFormat(_slot, id),
						ViewDimension = UavDimension.Texture2D,
						Anonymous = new UnorderedAccessViewDescUnion { Texture2D = new Tex2DUav { MipSlice = 0, PlaneSlice = 0 } }
					};
				}
				_ctx.Device.CreateUnorderedAccessView(_transients.Resource(_slot, id), default(ComPtr<ID3D12Resource>), &uav, _ring.Offset(cpu, (uint)i));
			}
			_cmd.SetComputeRootDescriptorTable((uint)baked.UavTableParam, gpu);
		}

		_cmd.Dispatch(groups.X, groups.Y, groups.Z);
	}

	public void DrawInstanced(string technique, BufferHandle instances, int vertexCount, int instanceCount)
	{
		if (_transients is null)
			throw new InvalidOperationException("DirectX12: a GPU-buffer instanced draw requires an active graph (SetGraph).");

		var baked = _techniques.GetOrBake(technique, _colorFormats.AsSpan(0, _colorCount), _depthFormat, _depthReadOnly);
		_cmd.SetGraphicsRootSignature(baked.RootSignature);
		_cmd.SetPipelineState(baked.Pipeline);

		var mvp = Camera.ViewProjection;
		if (baked.RootConstantParam >= 0)
			_cmd.SetGraphicsRoot32BitConstants((uint)baked.RootConstantParam, 16, &mvp, 0);

		// The buffer IS the per-instance vertex stream (one element per instance); the graph put it in
		// VERTEX_AND_CONSTANT_BUFFER before this pass. Stride comes from the technique's declared instance layout;
		// the technique fabricates vertexCount verts per instance from SV_VertexID (no geometry vertex buffer).
		var vbv = new VertexBufferView
		{
			BufferLocation = _transients.Resource(_slot, instances.Id).GetGPUVirtualAddress(),
			SizeInBytes = _transients.ByteSize(instances.Id),
			StrideInBytes = (uint)baked.InstanceStride,
		};
		_cmd.IASetPrimitiveTopology(D3DPrimitiveTopology.D3DPrimitiveTopologyTrianglelist);
		_cmd.IASetVertexBuffers(0, 1, &vbv);
		_cmd.DrawInstanced((uint)vertexCount, (uint)instanceCount, 0, 0);
	}

	public void DrawInstanced<T>(Mesh mesh, string technique, ReadOnlySpan<T> instances) where T : unmanaged
	{
		if (instances.Length == 0)
			return;
		var baked = _techniques.GetOrBake(technique, _colorFormats.AsSpan(0, _colorCount), _depthFormat, _depthReadOnly);
		_cmd.SetGraphicsRootSignature(baked.RootSignature);
		_cmd.SetPipelineState(baked.Pipeline);

		var mvp = Camera.ViewProjection; // the per-instance world matrix rides in the instance stream
		if (baked.RootConstantParam >= 0)
			_cmd.SetGraphicsRoot32BitConstants((uint)baked.RootConstantParam, 16, &mvp, 0);

		var bytes = System.Runtime.InteropServices.MemoryMarshal.AsBytes(instances);
		var gpuAddress = _instanceUpload.Upload(_slot, bytes);
		var instanceView = new VertexBufferView
		{
			BufferLocation = gpuAddress,
			SizeInBytes = (uint)bytes.Length,
			StrideInBytes = (uint)System.Runtime.CompilerServices.Unsafe.SizeOf<T>(),
		};
		_meshes.DrawInstanced(_cmd, mesh.Id, instanceView, (uint)instances.Length);
	}

	public void DrawInstanced(Mesh mesh, string technique, BufferHandle instances, int count)
	{
		if (_transients is null)
			throw new InvalidOperationException("DirectX12: instancing from a buffer requires an active graph (SetGraph).");
		var baked = _techniques.GetOrBake(technique, _colorFormats.AsSpan(0, _colorCount), _depthFormat, _depthReadOnly);
		_cmd.SetGraphicsRootSignature(baked.RootSignature);
		_cmd.SetPipelineState(baked.Pipeline);

		var mvp = Camera.ViewProjection;
		if (baked.RootConstantParam >= 0)
			_cmd.SetGraphicsRoot32BitConstants((uint)baked.RootConstantParam, 16, &mvp, 0);

		// The instance stream is a graph storage buffer a compute filled; the pass's .ReadsVertex derived the
		// transition to VERTEX_AND_CONSTANT_BUFFER. Take the per-instance stride from the technique's declared
		// instance layout — the same source of truth Vulkan binds from — rather than dividing total bytes by
		// count (which diverges when the buffer is over-allocated or the element has trailing padding).
		var byteSize = _transients.ByteSize(instances.Id);
		var instanceView = new VertexBufferView
		{
			BufferLocation = _transients.Resource(_slot, instances.Id).GetGPUVirtualAddress(),
			SizeInBytes = byteSize,
			StrideInBytes = baked.InstanceStride > 0 ? (uint)baked.InstanceStride : (count > 0 ? byteSize / (uint)count : byteSize),
		};
		_meshes.DrawInstanced(_cmd, mesh.Id, instanceView, (uint)count);
	}

	public void DispatchSkin(string technique, Mesh skinnedMesh, ReadOnlySpan<Matrix4x4> palette, BufferHandle output, int vertexCount)
	{
		if (_transients is null)
			throw new InvalidOperationException("DirectX12: skinning requires an active graph (SetGraph).");
		if (vertexCount <= 0 || palette.Length == 0)
			return;

		var baked = _techniques.GetOrBake(technique, ReadOnlySpan<DxgiFormat>.Empty, DxgiFormat.FormatUnknown, false);
		_cmd.SetComputeRootSignature(baked.RootSignature);
		_cmd.SetPipelineState(baked.Pipeline);

		// vertexCount rides the compute root constant (as a float — exact for any realistic count).
		if (baked.RootConstantParam >= 0)
		{
			var count = (float)vertexCount;
			_cmd.SetComputeRoot32BitConstants((uint)baked.RootConstantParam, 1, &count, 0);
		}

		// All three buffers bind as root descriptors by GPU address — no descriptor heap. They are raw
		// ByteAddressBuffers (the skin shader Loads/Stores at explicit byte offsets), so the output is a root UAV
		// (stride 0): t0 = bind-pose verts, t1 = bone palette, u0 = output.
		var paletteBytes = System.Runtime.InteropServices.MemoryMarshal.AsBytes(palette);
		var paletteAddress = _storageUpload.Upload(_slot, paletteBytes);
		_cmd.SetComputeRootShaderResourceView((uint)baked.InputBufferBaseParam, _meshes.BindPoseAddress(skinnedMesh.Id));
		_cmd.SetComputeRootShaderResourceView((uint)(baked.InputBufferBaseParam + 1), paletteAddress);
		_cmd.SetComputeRootUnorderedAccessView((uint)baked.UavRootParam, _transients.Resource(_slot, output.Id).GetGPUVirtualAddress());

		_cmd.Dispatch((uint)((vertexCount + 63) / 64), 1, 1);
	}

	public void DrawSkinned(Mesh mesh, BufferHandle skinnedVertices, string technique) => DrawSkinned(mesh, skinnedVertices, technique, Camera);

	public void DrawSkinned(Mesh mesh, BufferHandle skinnedVertices, string technique, Camera view)
	{
		if (_transients is null)
			throw new InvalidOperationException("DirectX12: skinned draw requires an active graph (SetGraph).");
		var baked = _techniques.GetOrBake(technique, _colorFormats.AsSpan(0, _colorCount), _depthFormat, _depthReadOnly);
		_cmd.SetGraphicsRootSignature(baked.RootSignature);
		_cmd.SetPipelineState(baked.Pipeline);

		var mvp = view.ViewProjection;
		if (baked.RootConstantParam >= 0)
			_cmd.SetGraphicsRoot32BitConstants((uint)baked.RootConstantParam, 16, &mvp, 0);

		_meshes.DrawSkinned(_cmd, mesh.Id, SkinnedView(skinnedVertices));
	}

	public void DrawSkinned(Mesh mesh, BufferHandle skinnedVertices, Material material, string technique) => DrawSkinned(mesh, skinnedVertices, material, technique, Camera);

	public void DrawSkinned(Mesh mesh, BufferHandle skinnedVertices, Material material, string technique, Camera view)
	{
		if (_transients is null)
			throw new InvalidOperationException("DirectX12: skinned draw requires an active graph (SetGraph).");
		var baked = _techniques.GetOrBake(technique, _colorFormats.AsSpan(0, _colorCount), _depthFormat, _depthReadOnly);
		_cmd.SetGraphicsRootSignature(baked.RootSignature);
		_cmd.SetPipelineState(baked.Pipeline);

		var mvp = view.ViewProjection;
		if (baked.RootConstantParam >= 0)
			_cmd.SetGraphicsRoot32BitConstants((uint)baked.RootConstantParam, 16, &mvp, 0);

		if (baked.TableParam >= 0 && baked.InputCount > 0)
			BindMaterialTable(baked, material, technique);

		_meshes.DrawSkinned(_cmd, mesh.Id, SkinnedView(skinnedVertices));
	}

	// The compute pre-skin output as a per-vertex stream: a MeshStandard-strided view over the transient buffer.
	private VertexBufferView SkinnedView(BufferHandle skinnedVertices) => new()
	{
		BufferLocation = _transients!.Resource(_slot, skinnedVertices.Id).GetGPUVirtualAddress(),
		SizeInBytes = _transients.ByteSize(skinnedVertices.Id),
		StrideInBytes = (uint)VertexLayout.MeshStandard.Stride,
	};

	public void DrawInstanced<T>(Mesh mesh, Material material, string technique, ReadOnlySpan<T> instances) where T : unmanaged
	{
		if (instances.Length == 0)
			return;
		var baked = _techniques.GetOrBake(technique, _colorFormats.AsSpan(0, _colorCount), _depthFormat, _depthReadOnly);
		_cmd.SetGraphicsRootSignature(baked.RootSignature);
		_cmd.SetPipelineState(baked.Pipeline);

		var mvp = Camera.ViewProjection; // the per-instance world data rides in the instance stream
		if (baked.RootConstantParam >= 0)
			_cmd.SetGraphicsRoot32BitConstants((uint)baked.RootConstantParam, 16, &mvp, 0);

		// Material atlas → the technique's reflected SRV table, identical to Draw(mesh, material); every instance
		// samples the shared atlas, the per-instance uv-rect (in the stream) picks each quad's region.
		if (baked.TableParam >= 0 && baked.InputCount > 0)
			BindMaterialTable(baked, material, technique);

		var bytes = System.Runtime.InteropServices.MemoryMarshal.AsBytes(instances);
		var gpuAddress = _instanceUpload.Upload(_slot, bytes);
		var instanceView = new VertexBufferView
		{
			BufferLocation = gpuAddress,
			SizeInBytes = (uint)bytes.Length,
			StrideInBytes = (uint)System.Runtime.CompilerServices.Unsafe.SizeOf<T>(),
		};
		_meshes.DrawInstanced(_cmd, mesh.Id, instanceView, (uint)instances.Length);
	}

	public void DrawInstanced<T>(Mesh mesh, Material material, string technique, ReadOnlySpan<T> instances, ReadOnlySpan<float> uniforms) where T : unmanaged
	{
		if (instances.Length == 0)
			return;
		var baked = _techniques.GetOrBake(technique, _colorFormats.AsSpan(0, _colorCount), _depthFormat, _depthReadOnly);
		_cmd.SetGraphicsRootSignature(baked.RootSignature);
		_cmd.SetPipelineState(baked.Pipeline);

		var mvp = Camera.ViewProjection;
		if (baked.RootConstantParam >= 0)
			_cmd.SetGraphicsRoot32BitConstants((uint)baked.RootConstantParam, 16, &mvp, 0);

		// The per-frame uniform block (M15-0) — e.g. foliage wind, read in the vertex stage — bound as the root
		// CBV (b1), alongside the material's alpha-test texture. The MVP still rides the b0 root constants.
		if (uniforms.Length > 0 && baked.CbvRootParam >= 0)
			BindUniforms(baked, uniforms);
		if (baked.TableParam >= 0 && baked.InputCount > 0)
			BindMaterialTable(baked, material, technique);

		var bytes = System.Runtime.InteropServices.MemoryMarshal.AsBytes(instances);
		var gpuAddress = _instanceUpload.Upload(_slot, bytes);
		var instanceView = new VertexBufferView
		{
			BufferLocation = gpuAddress,
			SizeInBytes = (uint)bytes.Length,
			StrideInBytes = (uint)System.Runtime.CompilerServices.Unsafe.SizeOf<T>(),
		};
		_meshes.DrawInstanced(_cmd, mesh.Id, instanceView, (uint)instances.Length);
	}

	public void DrawInstanced<T>(Mesh mesh, string technique, ReadOnlySpan<T> instances, ReadOnlySpan<ResourceHandle> sampled) where T : unmanaged
	{
		if (instances.Length == 0)
			return;
		var baked = _techniques.GetOrBake(technique, _colorFormats.AsSpan(0, _colorCount), _depthFormat, _depthReadOnly);
		_cmd.SetGraphicsRootSignature(baked.RootSignature);
		_cmd.SetPipelineState(baked.Pipeline);

		var mvp = Camera.ViewProjection;
		if (baked.RootConstantParam >= 0)
			_cmd.SetGraphicsRoot32BitConstants((uint)baked.RootConstantParam, 16, &mvp, 0);

		// Sampled graph resources (the G-buffer) → the technique's SRV table, identical to FullScreen: each
		// instanced light volume samples the normal/albedo at its covered pixel. The graph put them in PixelShaderResource.
		if (sampled.Length > 0)
			BindInputs(baked, sampled);

		var bytes = System.Runtime.InteropServices.MemoryMarshal.AsBytes(instances);
		var gpuAddress = _instanceUpload.Upload(_slot, bytes);
		var instanceView = new VertexBufferView
		{
			BufferLocation = gpuAddress,
			SizeInBytes = (uint)bytes.Length,
			StrideInBytes = (uint)System.Runtime.CompilerServices.Unsafe.SizeOf<T>(),
		};
		_meshes.DrawInstanced(_cmd, mesh.Id, instanceView, (uint)instances.Length);
	}

	public void DrawInstanced(string technique, Material material, BufferHandle instances, int vertexCount, int instanceCount)
	{
		if (_transients is null)
			throw new InvalidOperationException("DirectX12: a GPU-buffer instanced draw requires an active graph (SetGraph).");

		var baked = _techniques.GetOrBake(technique, _colorFormats.AsSpan(0, _colorCount), _depthFormat, _depthReadOnly);
		_cmd.SetGraphicsRootSignature(baked.RootSignature);
		_cmd.SetPipelineState(baked.Pipeline);

		var mvp = Camera.ViewProjection;
		if (baked.RootConstantParam >= 0)
			_cmd.SetGraphicsRoot32BitConstants((uint)baked.RootConstantParam, 16, &mvp, 0);

		// The atlas → reflected SRV table, so the fabricated quads can sub-UV sample per frame.
		if (baked.TableParam >= 0 && baked.InputCount > 0)
			BindMaterialTable(baked, material, technique);

		var vbv = new VertexBufferView
		{
			BufferLocation = _transients.Resource(_slot, instances.Id).GetGPUVirtualAddress(),
			SizeInBytes = _transients.ByteSize(instances.Id),
			StrideInBytes = (uint)baked.InstanceStride,
		};
		_cmd.IASetPrimitiveTopology(D3DPrimitiveTopology.D3DPrimitiveTopologyTrianglelist);
		_cmd.IASetVertexBuffers(0, 1, &vbv);
		_cmd.DrawInstanced((uint)vertexCount, (uint)instanceCount, 0, 0);
	}

	public void DrawList<TVertex>(string technique, ReadOnlySpan<Texture> textures, ReadOnlySpan<TVertex> vertices, ReadOnlySpan<ushort> indices, ReadOnlySpan<DrawCommand> commands, ReadOnlySpan<float> push) where TVertex : unmanaged
	{
		if (commands.IsEmpty || vertices.IsEmpty || indices.IsEmpty || textures.IsEmpty)
			return;
		var baked = _techniques.GetOrBake(technique, _colorFormats.AsSpan(0, _colorCount), _depthFormat, _depthReadOnly);
		_cmd.SetGraphicsRootSignature(baked.RootSignature);
		_cmd.SetPipelineState(baked.Pipeline);

		// Stage this frame's geometry into the dynamic rings (bump-allocated: several draw lists in one frame
		// append at successive offsets rather than overwrite each other).
		var vtxBytes = System.Runtime.InteropServices.MemoryMarshal.AsBytes(vertices);
		var idxBytes = System.Runtime.InteropServices.MemoryMarshal.AsBytes(indices);
		var vtxAddr = _listVtx.Upload(_slot, vtxBytes);
		var idxAddr = _listIdx.Upload(_slot, idxBytes);

		var vbv = new VertexBufferView { BufferLocation = vtxAddr, SizeInBytes = (uint)vtxBytes.Length, StrideInBytes = (uint)System.Runtime.CompilerServices.Unsafe.SizeOf<TVertex>() };
		var ibv = new IndexBufferView { BufferLocation = idxAddr, SizeInBytes = (uint)idxBytes.Length, Format = DxgiFormat.FormatR16Uint };
		_cmd.IASetPrimitiveTopology(D3DPrimitiveTopology.D3DPrimitiveTopologyTrianglelist);
		_cmd.IASetVertexBuffers(0, 1, &vbv);
		_cmd.IASetIndexBuffer(&ibv);

		// Negative-height viewport (origin at the bottom) so the same +Y-down clip math as the Vulkan backend applies.
		var viewport = new Viewport { TopLeftX = 0, TopLeftY = _fbHeight, Width = _fbWidth, Height = -(float)_fbHeight, MinDepth = 0, MaxDepth = 1 };
		_cmd.RSSetViewports(1, &viewport);

		// The shared per-list push fills the block's first slots; each command rewrites the slot right after it with
		// its tag (the draw-list contract), then re-pushes the whole block (push count = the used slots + the tag).
		var block = stackalloc float[8];
		var pushLen = Math.Min(push.Length, 7);
		for (var i = 0; i < pushLen; i++)
			block[i] = push[i];

		foreach (var c in commands)
		{
			var clip = c.ClipRect;
			var minX = MathF.Max(0, clip.X);
			var minY = MathF.Max(0, clip.Y);
			var maxX = MathF.Min(_fbWidth, clip.X + clip.Width);
			var maxY = MathF.Min(_fbHeight, clip.Y + clip.Height);
			if (maxX <= minX || maxY <= minY)
				continue;

			*(uint*)(block + pushLen) = c.Tag;
			if (baked.RootConstantParam >= 0)
				_cmd.SetGraphicsRoot32BitConstants((uint)baked.RootConstantParam, (uint)(pushLen + 1), block, 0);

			// Write the bound texture's SRV (real format + full mip chain) into a fresh ring slot, then bind the table.
			if (baked.TableParam >= 0)
			{
				var (cpu, gpu) = _ring.Allocate(1);
				WriteListTextureSrv(textures[Math.Clamp(c.TextureIndex, 0, textures.Length - 1)].Id, cpu);
				_cmd.SetGraphicsRootDescriptorTable((uint)baked.TableParam, gpu);
			}

			var scissor = new Silk.NET.Maths.Box2D<int>((int)minX, (int)minY, (int)maxX, (int)maxY);
			_cmd.RSSetScissorRects(1, &scissor);
			_cmd.DrawIndexedInstanced((uint)c.IndexCount, 1, (uint)c.IndexOffset, c.VertexOffset, 0);
		}
	}

	/// <summary>Resolve an inspectable graph resource to a draw-list-bindable texture handle for this frame (resource
	/// viewer). The id is offset by <see cref="InspectableResource.DebugTextureBase"/> so <see cref="DrawList{TVertex}"/> samples the live
	/// transient image rather than a loaded texture; the dimensions are the resource's current size.</summary>
	public Texture? ResolveInspectable(int resourceId)
	{
		if (_transients is null || resourceId < 0)
			return null;
		var (w, h) = _transients.Extent(resourceId);
		return new Texture(resourceId + InspectableResource.DebugTextureBase, (int)w, (int)h);
	}

	public void Dispose()
	{
		_instanceUpload.Dispose();
		_uniformUpload.Dispose();
		_storageUpload.Dispose();
		_listVtx.Dispose();
		_listIdx.Dispose();
	}
}
