using System.Numerics;

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
internal sealed unsafe class DX12RenderContext : IRenderContext, IDisposable
{
	private const string NextSlice = "DX12: this render verb lands in a following executor slice.";
	private const uint DefaultShader4ComponentMapping = 0x1688; // D3D12_DEFAULT_SHADER_4_COMPONENT_MAPPING

	private readonly DX12Context _ctx;
	private readonly DX12TechniqueCache _techniques;
	private readonly DX12DescriptorRing _ring;
	private readonly DX12MeshStore _meshes;
	private readonly DX12TextureStore _textures;
	private readonly DX12MaterialStore _materials;
	private readonly DX12InstanceUploadBuffer _instanceUpload;
	private DX12Transients? _transients;
#if LOOM_IMGUI
	// The host's ImGui renderer (backend-specific), wired in by the engine when ImGui is enabled. The HUD pass's
	// ctx.DrawImGui() forwards this frame's draw data to it. Null when EnableImGui hasn't been called.
	private DX12ImGuiRenderer? _imgui;
#endif

	private ComPtr<ID3D12GraphicsCommandList> _cmd;
	private uint _slot;
	// M13 resource viewer: resources to show as thumbnails this frame (the engine draws the images; ImGui.Image
	// no-ops on win-arm64). Populated by RequestThumbnail as the host enumerates resources, drawn + cleared each HUD pass.
	private readonly List<int> _thumbRequests = [];
	// M13 frustum-freeze wireframe: screen-space line segments (x0,y0,x1,y1 in pixels) the host sets each frame.
	private Vector4[] _debugLines = [];
	private int _debugLineCount;
	private const uint DebugLineColor = 0xFF00FFFFu; // IM_COL32 yellow
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

	public DX12RenderContext(DX12Context ctx, DX12TechniqueCache techniques, DX12DescriptorRing ring, DX12MeshStore meshes, DX12TextureStore textures, DX12MaterialStore materials, int frameCount)
	{
		_ctx = ctx;
		_techniques = techniques;
		_ring = ring;
		_meshes = meshes;
		_textures = textures;
		_materials = materials;
		_instanceUpload = new DX12InstanceUploadBuffer(ctx, frameCount);
	}

	/// <summary>Bind the active graph's transient targets (the source of sampled-input SRVs). Called from SetGraph.</summary>
	public void SetTransients(DX12Transients transients) => _transients = transients;

	/// <summary>Request that <paramref name="resourceId"/> be shown as a thumbnail in this frame's resource-viewer
	/// grid (M13). Called by the engine's GetDebugTextureId as the host enumerates resources to display.</summary>
	public void RequestThumbnail(int resourceId)
	{
		if (!_thumbRequests.Contains(resourceId))
			_thumbRequests.Add(resourceId);
	}

	/// <summary>Set this frame's debug line segments (screen pixels, x0,y0,x1,y1) — the frustum-freeze wireframe the
	/// HUD pass draws as an overlay (M13). Pass an empty span to clear. Overwritten each frame by the host.</summary>
	public void SetDebugLines(ReadOnlySpan<Vector4> segments)
	{
		if (_debugLines.Length < segments.Length)
			_debugLines = new Vector4[segments.Length];
		segments.CopyTo(_debugLines);
		_debugLineCount = segments.Length;
	}

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
		_instanceUpload.BeginFrame(slot); // reset this slot's per-frame instance staging
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
		{
			var textures = _materials.TexturesOf(material.Id);
			if (textures.Length < baked.InputCount)
				throw new InvalidOperationException($"DX12: material supplies {textures.Length} textures but technique '{technique}' samples {baked.InputCount}.");
			var (cpu, gpu) = _ring.Allocate((uint)baked.InputCount);
			for (var i = 0; i < baked.InputCount; i++)
				WriteTextureSrv(textures[i], _ring.Offset(cpu, (uint)i));
			_cmd.SetGraphicsRootDescriptorTable((uint)baked.TableParam, gpu);
		}

		_meshes.Draw(_cmd, mesh.Id);
	}

	// Create a Texture2D SRV over a loaded texture, exposing its full mip chain (unlike the transient WriteSrv,
	// which is single-mip). Material textures sample through this.
	private void WriteTextureSrv(int textureId, CpuDescriptorHandle dest)
	{
		var srv = new ShaderResourceViewDesc
		{
			Format = _textures.SrvFormat(textureId),
			ViewDimension = SrvDimension.Texture2D,
			Shader4ComponentMapping = DefaultShader4ComponentMapping,
			Anonymous = new ShaderResourceViewDescUnion
			{
				Texture2D = new Tex2DSrv { MostDetailedMip = 0, MipLevels = _textures.MipLevels(textureId), PlaneSlice = 0, ResourceMinLODClamp = 0f }
			}
		};
		_ctx.Device.CreateShaderResourceView(_textures.Resource(textureId), &srv, dest);
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
			throw new InvalidOperationException("DX12: instancing from a buffer requires an active graph (SetGraph).");
		var baked = _techniques.GetOrBake(technique, _colorFormats.AsSpan(0, _colorCount), _depthFormat, _depthReadOnly);
		_cmd.SetGraphicsRootSignature(baked.RootSignature);
		_cmd.SetPipelineState(baked.Pipeline);

		var mvp = Camera.ViewProjection;
		if (baked.RootConstantParam >= 0)
			_cmd.SetGraphicsRoot32BitConstants((uint)baked.RootConstantParam, 16, &mvp, 0);

		// The instance stream is a graph storage buffer a compute filled; the pass's .ReadsVertex derived the
		// transition to VERTEX_AND_CONSTANT_BUFFER. The stride is the buffer divided across its instances.
		var byteSize = _transients.ByteSize(instances.Id);
		var instanceView = new VertexBufferView
		{
			BufferLocation = _transients.Resource(_slot, instances.Id).GetGPUVirtualAddress(),
			SizeInBytes = byteSize,
			StrideInBytes = count > 0 ? byteSize / (uint)count : byteSize,
		};
		_meshes.DrawInstanced(_cmd, mesh.Id, instanceView, (uint)count);
	}

	// Forward this frame's ImGui draw data to the backend renderer, recording into the (already-open) HUD-pass
	// scope (the engine has bound the backbuffer RTV + negative-height viewport). No-op when ImGui isn't enabled
	// or the host produced no draw data this frame — and a compiled-out no-op when built without ImGui.
	public void DrawImGui()
	{
#if LOOM_IMGUI
		if (_imgui is null)
			return;

		// The ImGui UI (menu, windows, the resource-viewer name list) first.
		_imgui.Render(_cmd, _imguiDrawData, _fbWidth, _fbHeight, _slot);

		// M13 resource viewer: draw the requested resources as a thumbnail grid (engine-drawn — ImGui.Image no-ops on
		// win-arm64). The injected HUD .Samples edge put each in PixelShaderResource — sampling a live image. Laid
		// out to the right of the name list.
		if (_thumbRequests.Count > 0 && _transients is not null)
		{
			const float x0 = 300f, y0 = 30f, w = 200f, h = 112f, gap = 10f;
			var cols = Math.Max(1, (int)((_fbWidth - x0 - 10f) / (w + gap)));
			var thumbs = new DX12ImGuiRenderer.Thumbnail[_thumbRequests.Count];
			for (var i = 0; i < _thumbRequests.Count; i++)
			{
				var id = _thumbRequests[i];
				var gx = x0 + i % cols * (w + gap);
				var gy = y0 + i / cols * (h + gap);
				thumbs[i] = new DX12ImGuiRenderer.Thumbnail(_transients.Resource(_slot, id), _transients.SrvFormat(_slot, id), gx, gy, w, h);
			}
			_imgui.DrawThumbnails(_cmd, thumbs, _fbWidth, _fbHeight, _slot);
			_thumbRequests.Clear();
		}

		// M13 frustum-freeze wireframe: the host projected the frozen frustum's edges to screen pixels; draw them.
		if (_debugLineCount > 0)
			_imgui.DrawLines(_cmd, _debugLines.AsSpan(0, _debugLineCount), DebugLineColor, 2f, _fbWidth, _fbHeight, _slot);
#endif
	}

	// M13 resource viewer: GetDebugTextureId returns (resourceId + DebugTextureBase) — a non-zero id the host uses to
	// gate "inspectable?". The engine draws the matching thumbnail grid in the HUD pass.
	internal const int DebugTextureBase = 0x10000;

	public void Dispose() => _instanceUpload.Dispose();
}
