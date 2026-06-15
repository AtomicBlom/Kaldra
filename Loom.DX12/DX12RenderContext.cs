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
#if LOOM_IMGUI
	// The host's ImGui renderer (backend-specific), wired in by the engine when ImGui is enabled. The HUD pass's
	// ctx.DrawImGui() forwards this frame's draw data to it. Null when EnableImGui hasn't been called.
	private DX12ImGuiRenderer? _imgui;
#endif

	private ComPtr<ID3D12GraphicsCommandList> _cmd;
	private uint _slot;
	// This frame's ImGui draw data (ImDrawData*) + the framebuffer extent, captured in BeginFrame and consumed by
	// DrawImGui. Stored unconditionally (cheap values) so the seam exists even when built without ImGui.
	private nint _imguiDrawData;
	private uint _fbWidth;
	private uint _fbHeight;
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

#if LOOM_IMGUI
	/// <summary>The host's ImGui renderer (backend-specific), wired in by the engine when ImGui is enabled.</summary>
	public void SetImGui(DX12ImGuiRenderer renderer) => _imgui = renderer;
#endif

	public void BeginFrame(ComPtr<ID3D12GraphicsCommandList> cmd, FrameInputs inputs, bool hdrOutput, uint slot, uint fbWidth, uint fbHeight)
	{
		_cmd = cmd;
		_slot = slot;
		_imguiDrawData = inputs.ImGuiDrawData;
		_fbWidth = fbWidth;
		_fbHeight = fbHeight;
		DeltaTime = (float)inputs.DeltaSeconds;
		Time = inputs.Time;
		HoverId = inputs.HoverId;
		Camera = ClipSpace(inputs.Camera);
		SunView = ClipSpace(inputs.SunView);
		HdrOutput = hdrOutput;
	}

	// Adopt the Vulkan backend's clip-space convention (negate the projection's Y row), so the shared shaders need
	// NO per-backend branch — ndc.y matches the +Y-down texture V everywhere (mesh MVP, fullscreen reprojection,
	// the JFA's SV_Position). On its own this flips D3D12 upside down; the engine pairs it with a negative-height
	// viewport (DX12Engine.RecordRasterPass) that turns D3D12's (1-ndc.y) screen map into Vulkan's (ndc.y+1), so
	// geometry stays upright. The host supplies a neutral +Y-up projection in FrameInputs (M7-B5); both backends
	// flip it the same way here. (Earlier "identity ingest, no Y-flip" rendered mesh upright but left every
	// fullscreen pass vertically mirrored vs Vulkan — one mesh→fullscreen→backbuffer hop = upside down.)
	private static Camera ClipSpace(Camera camera)
	{
		var projection = camera.Projection;
		projection.M22 *= -1f;
		return new Camera(camera.View, projection);
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
				throw new NotSupportedException("DX12: sampling the backbuffer or a buffer resource lands in a later slice.");
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
			throw new InvalidOperationException("DX12: compute requires an active graph (SetGraph).");
		if (storage.Length > 1)
			throw new NotSupportedException("DX12: a compute pass binds one storage resource for now (the particle buffer / the exposure image); multi-storage compute lands later.");

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

		// Storage (the graph put the resource in UNORDERED_ACCESS first):
		//  * a RW STRUCTURED buffer (the particle stream) or a RW texture (the 1×1 exposure image) binds a UAV
		//    DESCRIPTOR in the ring + a UAV table. The structured-buffer descriptor carries StructureByteStride +
		//    NumElements, which is what makes the shader's GetDimensions report the real count (a root UAV is a raw
		//    address with no metadata → GetDimensions 0 → the particle update early-outs every thread, writes nothing).
		//  * a RAW buffer (the FXC built-in's ByteAddressBuffer, no reflected stride) binds as a root UAV by address.
		if (storage.Length > 0)
		{
			var id = storage[0].Id;
			if (storage[0].Kind == ResourceKind.Buffer && baked.UavRootParam >= 0)
			{
				_cmd.SetComputeRootUnorderedAccessView((uint)baked.UavRootParam, _transients.Resource(_slot, id).GetGPUVirtualAddress());
			}
			else if (baked.UavTableParam >= 0)
			{
				var (cpu, gpu) = _ring.Allocate(1);
				UnorderedAccessViewDesc uav;
				if (storage[0].Kind == ResourceKind.Buffer)
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
				_ctx.Device.CreateUnorderedAccessView(_transients.Resource(_slot, id), default(ComPtr<ID3D12Resource>), &uav, cpu);
				_cmd.SetComputeRootDescriptorTable((uint)baked.UavTableParam, gpu);
			}
		}

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

	// Forward this frame's ImGui draw data to the backend renderer, recording into the (already-open) HUD-pass
	// scope (the engine has bound the backbuffer RTV + negative-height viewport). No-op when ImGui isn't enabled
	// or the host produced no draw data this frame — and a compiled-out no-op when built without ImGui.
	public void DrawImGui()
	{
#if LOOM_IMGUI
		_imgui?.Render(_cmd, _imguiDrawData, _fbWidth, _fbHeight, _slot);
#endif
	}
}
