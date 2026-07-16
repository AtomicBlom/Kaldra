using Loom.Core.Graph;
using Loom.Core.Hosting;
using Loom.Core.Materials;
using Loom.Core.Primitives;
using Loom.Graph;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D12;
using DxgiFormat = Silk.NET.DXGI.Format;

namespace Loom.Backend.DirectX12;

/// <summary>
/// The DirectX12 <see cref="IRenderContext"/> — the per-frame command recorder handed to each pass's
/// <c>.Draw</c> callback (the counterpart of the Vulkan backend's <c>RenderContext</c>). It is stateful: the
/// engine pushes the frame inputs + slot (<see cref="BeginFrame"/>) and, per pass, the actual render-target
/// formats + depth format (<see cref="BeginPass"/>) so a technique bakes a pipeline matching the pass's
/// attachments.
/// <para>
/// Implements the fullscreen verbs (from-scratch, sampling, and a scalar push block) and indexed mesh draw
/// (vertex/index buffers + an MVP root constant).
/// </para>
/// </summary>
internal sealed unsafe partial class DirectX12RenderContext : IRenderContext, IDisposable
{
	private const uint DefaultShader4ComponentMapping = 0x1688; // D3D12_DEFAULT_SHADER_4_COMPONENT_MAPPING

	private readonly DirectX12Context _ctx;
	private readonly DirectX12TechniqueCache _techniques;
	private readonly DirectX12DescriptorRing _ring;
	private readonly DirectX12MeshStore _meshes;
	private readonly DirectX12TextureStore _textures;
	private readonly MaterialStore _materials;
	private readonly DirectX12InstanceUploadBuffer _instanceUpload;
	// Per-frame UPLOAD-heap ring for the uniform seam (CSM matrices, lighting params, wind), 256-aligned
	// so each block is a valid CBV base. Bound as a root CBV at b1 on a technique that reflects a constant buffer.
	private readonly DirectX12InstanceUploadBuffer _uniformUpload;
	// Per-frame UPLOAD ring for the bone palette the compute pre-skin pass reads as a structured-buffer
	// root SRV; 256-aligned (a multiple of the 64-byte matrix stride, so each block is element-aligned).
	private readonly DirectX12InstanceUploadBuffer _storageUpload;
	// Per-frame UPLOAD rings for ctx.DrawList's dynamic geometry — a vertex ring and an index ring (typeless UPLOAD
	// buffers bound by GPU address), so several draw lists in one frame append at successive offsets rather than clobber.
	private readonly DirectX12InstanceUploadBuffer _listVtx;
	private readonly DirectX12InstanceUploadBuffer _listIdx;
	// Renderer-private offscreen targets backing UI group-opacity layers (BeginLayer/EndLayer), below the graph seam.
	private readonly DirectX12LayerTargets _layerTargets;
	// Reused across calls so an opacity layer never allocates per frame.
	private readonly List<(int Depth, Half4 Tint)> _layerStack = [];
	// The current pass's colour-target RTV handle(s) + extent, captured by the engine before the pass records, so a
	// draw-list layer can rebind (suspend/resume) the pass's render targets around its offscreen target.
	private readonly CpuDescriptorHandle[] _scopeRtvs = new CpuDescriptorHandle[8];
	private int _scopeRtvCount;
	private uint _scopeWidth, _scopeHeight;
	private DirectX12Transients? _transients;
	private ComPtr<ID3D12GraphicsCommandList> _cmd;
	private uint _slot;
	private uint _fbWidth;
	private uint _fbHeight;
	private DxgiFormat[] _colorFormats = [];
	private int _colorCount;
	private DxgiFormat _depthFormat;
	private bool _depthReadOnly;

	public double DeltaTime { get; private set; }
	public double Time { get; private set; }
	public bool HdrOutput { get; private set; }

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
		_layerTargets = new DirectX12LayerTargets(ctx, frameCount);
	}

	/// <summary>
	/// Capture the current raster pass's colour-target RTV handle(s) + extent before its <c>.Record</c> runs, so a
	/// <see cref="DrawList{TVertex}"/> group-opacity layer can rebind the pass's render targets after detouring into
	/// an offscreen target.
	/// </summary>
	public void SetColorScope(ReadOnlySpan<CpuDescriptorHandle> rtvs, uint width, uint height)
	{
		_scopeRtvCount = Math.Min(rtvs.Length, _scopeRtvs.Length);
		for (var i = 0; i < _scopeRtvCount; i++)
			_scopeRtvs[i] = rtvs[i];
		_scopeWidth = width;
		_scopeHeight = height;
	}

	/// <summary>Bind the active graph's transient targets (the source of sampled-input SRVs). Called from SetGraph.</summary>
	public void SetTransients(DirectX12Transients transients) => _transients = transients;

	public void BeginFrame(ComPtr<ID3D12GraphicsCommandList> cmd, FrameInputs inputs, bool hdrOutput, uint slot, uint fbWidth, uint fbHeight)
	{
		_cmd = cmd;
		_slot = slot;
		_fbWidth = fbWidth;
		_fbHeight = fbHeight;
		DeltaTime = inputs.DeltaSeconds;
		Time = inputs.Time;
		HdrOutput = hdrOutput;
		_instanceUpload.BeginFrame(slot); // reset this slot's per-frame instance staging
		_uniformUpload.BeginFrame(slot);  // and its per-frame uniform staging
		_storageUpload.BeginFrame(slot);  // and the bone-palette staging
		_listVtx.BeginFrame(slot);        // and the draw-list geometry staging
		_listIdx.BeginFrame(slot);
	}

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
		// in-closure push then fills the next CBV (b(K+1)) if a reflected slot remains (no 16-float cap),
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
		_layerTargets.Dispose();
	}
}
