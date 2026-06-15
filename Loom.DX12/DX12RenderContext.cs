using Loom.Core;

using Silk.NET.Core.Native;
using Silk.NET.Direct3D12;

using DxgiFormat = Silk.NET.DXGI.Format;

namespace Loom.DX12;

/// <summary>
/// The DX12 <see cref="IRenderContext"/> — the per-frame command recorder handed to each pass's
/// <c>.Draw</c> callback (the counterpart of the Vulkan backend's <c>RenderContext</c>). It is stateful: the
/// engine pushes the frame inputs + slot (<see cref="BeginFrame"/>) and, per pass, the actual render-target
/// formats + depth format (<see cref="BeginPass"/>) so a technique bakes a pipeline matching the pass's
/// attachments.
/// <para>
/// Implements the fullscreen verbs (from-scratch, sampling, and a scalar push block) and indexed mesh draw
/// (vertex/index buffers + an MVP root constant). Compute and ImGui land in following slices.
/// </para>
/// </summary>
internal sealed unsafe class DX12RenderContext : IRenderContext
{
	private const string NextSlice = "DX12: this render verb lands in a following executor slice.";
	private const uint DefaultShader4ComponentMapping = 0x1688; // D3D12_DEFAULT_SHADER_4_COMPONENT_MAPPING

	private readonly DX12Context _ctx;
	private readonly DX12TechniqueCache _techniques;
	private readonly DX12DescriptorRing _ring;
	private readonly DX12MeshStore _meshes;
	private DX12Transients? _transients;

	private ComPtr<ID3D12GraphicsCommandList> _cmd;
	private uint _slot;
	private DxgiFormat[] _colorFormats = [];
	private int _colorCount;
	private DxgiFormat _depthFormat;
	private bool _depthReadOnly;

	public float DeltaTime { get; private set; }
	public float Time { get; private set; }
	public uint HoverId { get; private set; }
	public Camera SunView { get; private set; } = Camera.Identity;
	public Camera Camera { get; private set; } = Camera.Identity;
	public bool HdrOutput { get; private set; }

	public DX12RenderContext(DX12Context ctx, DX12TechniqueCache techniques, DX12DescriptorRing ring, DX12MeshStore meshes)
	{
		_ctx = ctx;
		_techniques = techniques;
		_ring = ring;
		_meshes = meshes;
	}

	/// <summary>Bind the active graph's transient targets (the source of sampled-input SRVs). Called from SetGraph.</summary>
	public void SetTransients(DX12Transients transients) => _transients = transients;

	public void BeginFrame(ComPtr<ID3D12GraphicsCommandList> cmd, FrameInputs inputs, bool hdrOutput, uint slot)
	{
		_cmd = cmd;
		_slot = slot;
		DeltaTime = (float)inputs.DeltaSeconds;
		Time = inputs.Time;
		HoverId = inputs.HoverId;
		Camera = inputs.Camera;
		SunView = inputs.SunView;
		HdrOutput = hdrOutput;
	}

	/// <summary>
	/// Tell the context the current pass's colour-target formats + depth format (or <c>FormatUnknown</c> for no
	/// depth), so techniques bake matching pipelines. <paramref name="depthReadOnly"/> bakes with depth writes
	/// disabled (a <c>.ReadsDepth</c> pass tests against, but does not disturb, the opaque depth).
	/// </summary>
	public void BeginPass(ReadOnlySpan<DxgiFormat> colorFormats, DxgiFormat depthFormat, bool depthReadOnly)
	{
		if (_colorFormats.Length < colorFormats.Length)
			_colorFormats = new DxgiFormat[colorFormats.Length];
		colorFormats.CopyTo(_colorFormats);
		_colorCount = colorFormats.Length;
		_depthFormat = depthFormat;
		_depthReadOnly = depthReadOnly;
	}

	public void FullScreen(string technique, params ReadOnlySpan<ResourceHandle> inputs) =>
		FullScreen(technique, default, inputs);

	public void FullScreen(string technique, ReadOnlySpan<float> push, params ReadOnlySpan<ResourceHandle> inputs)
	{
		var baked = _techniques.GetOrBake(technique, _colorFormats.AsSpan(0, _colorCount), _depthFormat, _depthReadOnly);
		_cmd.SetGraphicsRootSignature(baked.RootSignature);
		_cmd.SetPipelineState(baked.Pipeline);

		if (push.Length > 0)
			SetPush(baked, push);
		if (inputs.Length > 0)
			BindInputs(baked, inputs);

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

	// Push a scalar block to the pass's root constants (the per-pass channel: JFA step, exposure rate, a
	// light-space matrix). Padded to the layout's 16 floats so the shader always reads defined memory.
	private void SetPush(DX12Technique baked, ReadOnlySpan<float> push)
	{
		if (baked.RootConstantParam < 0)
			throw new InvalidOperationException("DX12: technique has no root-constant block but a push was supplied.");
		var block = stackalloc float[16];
		for (var i = 0; i < push.Length && i < 16; i++)
			block[i] = push[i];
		_cmd.SetGraphicsRoot32BitConstants((uint)baked.RootConstantParam, 16, block, 0);
	}

	// Write the sampled inputs' SRVs into a fresh descriptor-ring range and bind it as the technique's SRV table.
	private void BindInputs(DX12Technique baked, ReadOnlySpan<ResourceHandle> inputs)
	{
		if (_transients is null)
			throw new InvalidOperationException("DX12: sampled inputs require an active graph (SetGraph).");
		if (baked.TableParam < 0)
			throw new InvalidOperationException("DX12: technique declares no SRV table but sampled inputs were supplied.");

		var (cpu, gpu) = _ring.Allocate((uint)inputs.Length);
		for (var i = 0; i < inputs.Length; i++)
		{
			var id = inputs[i].Id;
			if (!_transients.IsTransient(id))
				throw new NotSupportedException("DX12: sampling the backbuffer or a persistent/buffer resource lands in a later slice.");

			var srv = new ShaderResourceViewDesc
			{
				// Sample through the SRV-compatible format: R32_FLOAT for a depth target (a shadow map), whose
				// resource is TYPELESS and whose DSV format (D32_FLOAT) is not SRV-creatable; == the colour format otherwise.
				Format = _transients.SrvFormat(_slot, id),
				ViewDimension = SrvDimension.Texture2D,
				Shader4ComponentMapping = DefaultShader4ComponentMapping,
				Anonymous = new ShaderResourceViewDescUnion
				{
					Texture2D = new Tex2DSrv { MostDetailedMip = 0, MipLevels = 1, PlaneSlice = 0, ResourceMinLODClamp = 0f }
				}
			};
			_ctx.Device.CreateShaderResourceView(_transients.Resource(_slot, id), &srv, _ring.Offset(cpu, (uint)i));
		}
		_cmd.SetGraphicsRootDescriptorTable((uint)baked.TableParam, gpu);
	}

	public void Dispatch(string technique, (uint X, uint Y, uint Z) groups, ReadOnlySpan<float> push, ReadOnlySpan<ResourceHandle> sampled, ReadOnlySpan<ResourceHandle> storage)
	{
		if (_transients is null)
			throw new InvalidOperationException("DX12: compute requires an active graph (SetGraph).");
		if (sampled.Length > 0)
			throw new NotSupportedException("DX12: sampled inputs in a compute pass land with the offline-DXIL slice (SSAO/auto-exposure).");
		if (storage.Length > 1)
			throw new NotSupportedException("DX12: a compute pass binds one storage buffer for now (the GPU-particle path); multi-buffer compute lands later.");

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
		// The storage buffer binds as a root UAV (by GPU address) — the graph put it in UNORDERED_ACCESS first.
		if (storage.Length > 0 && baked.UavRootParam >= 0)
			_cmd.SetComputeRootUnorderedAccessView((uint)baked.UavRootParam, _transients.Resource(_slot, storage[0].Id).GetGPUVirtualAddress());

		_cmd.Dispatch(groups.X, groups.Y, groups.Z);
	}

	public void DrawParticles(ResourceHandle buffer, int count, string technique)
	{
		if (_transients is null)
			throw new InvalidOperationException("DX12: particles require an active graph (SetGraph).");

		var baked = _techniques.GetOrBake(technique, _colorFormats.AsSpan(0, _colorCount), _depthFormat, _depthReadOnly);
		_cmd.SetGraphicsRootSignature(baked.RootSignature);
		_cmd.SetPipelineState(baked.Pipeline);

		var mvp = Camera.ViewProjection;
		if (baked.RootConstantParam >= 0)
			_cmd.SetGraphicsRoot32BitConstants((uint)baked.RootConstantParam, 16, &mvp, 0);

		// The buffer IS the per-instance vertex stream (one float4 per particle); the graph put it in
		// VERTEX_AND_CONSTANT_BUFFER before this pass. 6 fabricated quad verts × one instance per particle.
		var vbv = new VertexBufferView
		{
			BufferLocation = _transients.Resource(_slot, buffer.Id).GetGPUVirtualAddress(),
			SizeInBytes = _transients.ByteSize(buffer.Id),
			StrideInBytes = 16,
		};
		_cmd.IASetPrimitiveTopology(D3DPrimitiveTopology.D3DPrimitiveTopologyTrianglelist);
		_cmd.IASetVertexBuffers(0, 1, &vbv);
		_cmd.DrawInstanced(6, (uint)count, 0, 0);
	}

	public void DrawImGui() { } // neutral no-op until the DX12 ImGui renderer lands
}
