// The DX12 Dear ImGui renderer is compiled only when the engine is built with ImGui (UseImGui=true). When off,
// the whole type is excluded and ImGui.NET is not referenced (Loom.DX12.csproj); the neutral ctx.DrawImGui() /
// EnableImGui() seam in Loom.Core stays, as documented no-ops. The DX12 twin of Loom.Vulkan.ImGuiRenderer.
#if LOOM_IMGUI
using System.Numerics;
using System.Runtime.InteropServices;

using ImGuiNET;

using Silk.NET.Core.Native;
using Silk.NET.Direct3D12;

using DxgiFormat = Silk.NET.DXGI.Format;

namespace Loom.DX12;

/// <summary>
/// Loom's DirectX 12 Dear ImGui renderer — the <c>imgui_impl_dx12</c> equivalent (the twin of the Vulkan
/// backend's <see cref="Loom.Vulkan"/>.<c>ImGuiRenderer</c>). ImGui rendering is inherently backend-specific
/// (it bakes a PSO, uploads a font atlas, and translates <c>ImDrawData</c> into draw calls), so it is
/// quarantined here in the backend; Loom.Core/Graph stay UI-agnostic, reaching it only through the neutral
/// <c>ctx.DrawImGui()</c> verb. The host owns the ImGui context + input and hands the executor the draw-data
/// pointer each frame; this class just consumes it.
/// <para>
/// It records <em>into</em> the already-open "hud" pass — which <c>.Blends</c> the backbuffer at native
/// resolution, so the UI is crisp and untouched by bloom/pixelation. The engine has already bound the
/// backbuffer RTV and set the negative-height viewport (DX12RenderContext's Vulkan-convention clip space);
/// this records vertices, PSO, and the scissored draw loop into that scope. The font atlas + its texture are
/// created once; the per-slot vertex/index buffers are UPLOAD-heap and grown on demand (the slot's fence has
/// been waited before the executor records it again, so reusing the buffer is hazard-free). The font SRV is
/// written into the engine's shader-visible descriptor ring each frame (the heap already bound for the frame).
/// </para>
/// </summary>
internal sealed unsafe class DX12ImGuiRenderer : IDisposable
{
	private readonly DX12Context _ctx;
	private readonly DX12DescriptorRing _ring;

	private ComPtr<ID3D12RootSignature> _rootSignature;
	private ComPtr<ID3D12PipelineState> _pipeline;
	private ComPtr<ID3D12Resource> _fontTexture;

	// Per frame-in-flight slot: an UPLOAD-heap (GENERIC_READ) vertex + index buffer, grown on demand (ImGui
	// geometry size varies with what the UI draws). Multi-buffered like the transients so a frame's upload can't
	// clobber a still-in-flight earlier frame's draw of its own slot.
	private readonly int _framesInFlight;
	private readonly ComPtr<ID3D12Resource>[] _vtxBuffers;
	private readonly ulong[] _vtxSizes;
	private readonly ComPtr<ID3D12Resource>[] _idxBuffers;
	private readonly ulong[] _idxSizes;

	private const uint PushCount = 4; // scale (float2) + translate (float2) — 4 DWORDs at b0

	// Per frame-in-flight slot: UPLOAD-heap vertex/index buffers for the resource-viewer thumbnail quads (M13),
	// grown on demand like the ImGui geometry buffers. The engine draws these instead of ImGui.Image.
	private readonly ComPtr<ID3D12Resource>[] _thumbVtx;
	private readonly ulong[] _thumbVtxSize;
	private readonly ComPtr<ID3D12Resource>[] _thumbIdx;
	private readonly ulong[] _thumbIdxSize;
	// Per-slot buffers for the debug line overlay (the frustum-freeze wireframe — M13). Each segment is a thin quad
	// sampling the font atlas's solid white texel, so the vertex colour is the line colour.
	private readonly ComPtr<ID3D12Resource>[] _lineVtx;
	private readonly ulong[] _lineVtxSize;
	private readonly ComPtr<ID3D12Resource>[] _lineIdx;
	private readonly ulong[] _lineIdxSize;
	private Vector2 _whiteUv; // ImGui font atlas's solid-white texel UV — solid fills sample it

	/// <summary>A textured quad the resource viewer draws (M13): a graph texture + its SRV format + a pixel rect.</summary>
	internal readonly record struct Thumbnail(ComPtr<ID3D12Resource> Resource, DxgiFormat SrvFormat, float X, float Y, float W, float H);

	/// <summary>
	/// Draw the resource-viewer thumbnails (M13): one textured quad per <see cref="Thumbnail"/> in pixel space, into
	/// the (already-open) HUD-pass scope after the ImGui UI. The engine draws these instead of <c>ImGui.Image</c>
	/// (ImGui.NET's by-value-ImVec2 marshalling no-ops on win-arm64). Reuses the ImGui PSO + a per-slot quad buffer.
	/// </summary>
	public void DrawThumbnails(ComPtr<ID3D12GraphicsCommandList> cmd, ReadOnlySpan<Thumbnail> thumbs, uint fbWidth, uint fbHeight, uint frameSlot)
	{
		if (thumbs.Length == 0)
			return;

		var vtxStride = (uint)sizeof(ImDrawVert);
		var vtxBytes = (ulong)(thumbs.Length * 4) * vtxStride;
		var idxBytes = (ulong)(thumbs.Length * 6) * sizeof(ushort);
		EnsureBuffer(ref _thumbVtx[frameSlot], ref _thumbVtxSize[frameSlot], vtxBytes);
		EnsureBuffer(ref _thumbIdx[frameSlot], ref _thumbIdxSize[frameSlot], idxBytes);

		const uint white = 0xFFFFFFFFu;
		void* vtxMapped;
		void* idxMapped;
		var noRead = new Silk.NET.Direct3D12.Range { Begin = 0, End = 0 };
		SilkMarshal.ThrowHResult(_thumbVtx[frameSlot].Map(0, &noRead, &vtxMapped));
		SilkMarshal.ThrowHResult(_thumbIdx[frameSlot].Map(0, &noRead, &idxMapped));
		var verts = (ImDrawVert*)vtxMapped;
		var indices = (ushort*)idxMapped;
		for (var i = 0; i < thumbs.Length; i++)
		{
			var t = thumbs[i];
			var v = verts + i * 4;
			v[0] = new ImDrawVert { pos = new Vector2(t.X, t.Y), uv = new Vector2(0, 0), col = white };
			v[1] = new ImDrawVert { pos = new Vector2(t.X + t.W, t.Y), uv = new Vector2(1, 0), col = white };
			v[2] = new ImDrawVert { pos = new Vector2(t.X + t.W, t.Y + t.H), uv = new Vector2(1, 1), col = white };
			v[3] = new ImDrawVert { pos = new Vector2(t.X, t.Y + t.H), uv = new Vector2(0, 1), col = white };
			var ix = indices + i * 6;
			var b = (ushort)(i * 4);
			ix[0] = b; ix[1] = (ushort)(b + 1); ix[2] = (ushort)(b + 2);
			ix[3] = b; ix[4] = (ushort)(b + 2); ix[5] = (ushort)(b + 3);
		}
		_thumbVtx[frameSlot].Unmap(0, (Silk.NET.Direct3D12.Range*)null);
		_thumbIdx[frameSlot].Unmap(0, (Silk.NET.Direct3D12.Range*)null);

		cmd.SetGraphicsRootSignature(_rootSignature);
		cmd.SetPipelineState(_pipeline);
		var vbv = new VertexBufferView { BufferLocation = _thumbVtx[frameSlot].GetGPUVirtualAddress(), SizeInBytes = (uint)vtxBytes, StrideInBytes = vtxStride };
		var ibv = new IndexBufferView { BufferLocation = _thumbIdx[frameSlot].GetGPUVirtualAddress(), SizeInBytes = (uint)idxBytes, Format = DxgiFormat.FormatR16Uint };
		cmd.IASetPrimitiveTopology(D3DPrimitiveTopology.D3DPrimitiveTopologyTrianglelist);
		cmd.IASetVertexBuffers(0, 1, &vbv);
		cmd.IASetIndexBuffer(&ibv);

		// Negative-height viewport + the same pixel→clip push as the ImGui render (no display offset).
		var viewport = new Viewport { TopLeftX = 0, TopLeftY = fbHeight, Width = fbWidth, Height = -(float)fbHeight, MinDepth = 0, MaxDepth = 1 };
		cmd.RSSetViewports(1, &viewport);
		var fullScissor = new Silk.NET.Maths.Box2D<int>(0, 0, (int)fbWidth, (int)fbHeight);
		cmd.RSSetScissorRects(1, &fullScissor);
		var push = stackalloc float[4];
		push[0] = 2.0f / fbWidth;
		push[1] = 2.0f / fbHeight;
		push[2] = -1.0f;
		push[3] = -1.0f;
		cmd.SetGraphicsRoot32BitConstants(0, PushCount, push, 0);

		for (var i = 0; i < thumbs.Length; i++)
		{
			var (cpu, gpu) = _ring.Allocate(1);
			var srvDesc = new ShaderResourceViewDesc
			{
				Format = thumbs[i].SrvFormat,
				ViewDimension = SrvDimension.Texture2D,
				Shader4ComponentMapping = 0x1688, // D3D12_DEFAULT_SHADER_4_COMPONENT_MAPPING
				Anonymous = new ShaderResourceViewDescUnion { Texture2D = new Tex2DSrv { MostDetailedMip = 0, MipLevels = 1, PlaneSlice = 0, ResourceMinLODClamp = 0f } },
			};
			_ctx.Device.CreateShaderResourceView(thumbs[i].Resource, &srvDesc, cpu);
			cmd.SetGraphicsRootDescriptorTable(1, gpu);
			cmd.DrawIndexedInstanced(6, 1, (uint)(i * 6), i * 4, 0);
		}
	}

	/// <summary>
	/// Draw screen-space debug line segments (M13 frustum-freeze wireframe) as thin quads in the HUD overlay. Each
	/// <paramref name="segments"/> entry is (x0,y0,x1,y1) in framebuffer pixels; <paramref name="color"/> is a packed
	/// IM_COL32. Samples the font atlas's white texel for a solid colour. Engine-drawn (ImGui's line helpers take
	/// by-value ImVec2, which no-ops on win-arm64).
	/// </summary>
	public void DrawLines(ComPtr<ID3D12GraphicsCommandList> cmd, ReadOnlySpan<Vector4> segments, uint color, float thickness, uint fbWidth, uint fbHeight, uint frameSlot)
	{
		if (segments.Length == 0)
			return;

		var vtxStride = (uint)sizeof(ImDrawVert);
		var vtxBytes = (ulong)(segments.Length * 4) * vtxStride;
		var idxBytes = (ulong)(segments.Length * 6) * sizeof(ushort);
		EnsureBuffer(ref _lineVtx[frameSlot], ref _lineVtxSize[frameSlot], vtxBytes);
		EnsureBuffer(ref _lineIdx[frameSlot], ref _lineIdxSize[frameSlot], idxBytes);

		var half = MathF.Max(0.5f, thickness * 0.5f);
		void* vtxMapped;
		void* idxMapped;
		var noRead = new Silk.NET.Direct3D12.Range { Begin = 0, End = 0 };
		SilkMarshal.ThrowHResult(_lineVtx[frameSlot].Map(0, &noRead, &vtxMapped));
		SilkMarshal.ThrowHResult(_lineIdx[frameSlot].Map(0, &noRead, &idxMapped));
		var verts = (ImDrawVert*)vtxMapped;
		var indices = (ushort*)idxMapped;
		for (var i = 0; i < segments.Length; i++)
		{
			var s = segments[i];
			var a = new Vector2(s.X, s.Y);
			var b = new Vector2(s.Z, s.W);
			var dir = b - a;
			var len = dir.Length();
			var perp = len > 1e-4f ? new Vector2(-dir.Y, dir.X) / len * half : new Vector2(0, half);
			var v = verts + i * 4;
			v[0] = new ImDrawVert { pos = a - perp, uv = _whiteUv, col = color };
			v[1] = new ImDrawVert { pos = b - perp, uv = _whiteUv, col = color };
			v[2] = new ImDrawVert { pos = b + perp, uv = _whiteUv, col = color };
			v[3] = new ImDrawVert { pos = a + perp, uv = _whiteUv, col = color };
			var ix = indices + i * 6;
			var bo = (ushort)(i * 4);
			ix[0] = bo; ix[1] = (ushort)(bo + 1); ix[2] = (ushort)(bo + 2);
			ix[3] = bo; ix[4] = (ushort)(bo + 2); ix[5] = (ushort)(bo + 3);
		}
		_lineVtx[frameSlot].Unmap(0, (Silk.NET.Direct3D12.Range*)null);
		_lineIdx[frameSlot].Unmap(0, (Silk.NET.Direct3D12.Range*)null);

		cmd.SetGraphicsRootSignature(_rootSignature);
		cmd.SetPipelineState(_pipeline);

		// The white-texel font SRV (the quads sample it for a solid colour).
		var (cpu, gpu) = _ring.Allocate(1);
		var srvDesc = new ShaderResourceViewDesc
		{
			Format = DxgiFormat.FormatR8G8B8A8Unorm,
			ViewDimension = SrvDimension.Texture2D,
			Shader4ComponentMapping = 0x1688,
			Anonymous = new ShaderResourceViewDescUnion { Texture2D = new Tex2DSrv { MostDetailedMip = 0, MipLevels = 1, PlaneSlice = 0, ResourceMinLODClamp = 0f } },
		};
		_ctx.Device.CreateShaderResourceView(_fontTexture, &srvDesc, cpu);
		cmd.SetGraphicsRootDescriptorTable(1, gpu);

		var vbv = new VertexBufferView { BufferLocation = _lineVtx[frameSlot].GetGPUVirtualAddress(), SizeInBytes = (uint)vtxBytes, StrideInBytes = vtxStride };
		var ibv = new IndexBufferView { BufferLocation = _lineIdx[frameSlot].GetGPUVirtualAddress(), SizeInBytes = (uint)idxBytes, Format = DxgiFormat.FormatR16Uint };
		cmd.IASetPrimitiveTopology(D3DPrimitiveTopology.D3DPrimitiveTopologyTrianglelist);
		cmd.IASetVertexBuffers(0, 1, &vbv);
		cmd.IASetIndexBuffer(&ibv);

		var viewport = new Viewport { TopLeftX = 0, TopLeftY = fbHeight, Width = fbWidth, Height = -(float)fbHeight, MinDepth = 0, MaxDepth = 1 };
		cmd.RSSetViewports(1, &viewport);
		var fullScissor = new Silk.NET.Maths.Box2D<int>(0, 0, (int)fbWidth, (int)fbHeight);
		cmd.RSSetScissorRects(1, &fullScissor);
		var push = stackalloc float[4];
		push[0] = 2.0f / fbWidth;
		push[1] = 2.0f / fbHeight;
		push[2] = -1.0f;
		push[3] = -1.0f;
		cmd.SetGraphicsRoot32BitConstants(0, PushCount, push, 0);

		cmd.DrawIndexedInstanced((uint)(segments.Length * 6), 1, 0, 0, 0);
	}

	public DX12ImGuiRenderer(DX12Context ctx, DX12DescriptorRing ring, byte[] vertexDxil, byte[] fragmentDxil, DxgiFormat rtvFormat, int framesInFlight)
	{
		_ctx = ctx;
		_ring = ring;
		_framesInFlight = framesInFlight;
		_vtxBuffers = new ComPtr<ID3D12Resource>[framesInFlight];
		_vtxSizes = new ulong[framesInFlight];
		_idxBuffers = new ComPtr<ID3D12Resource>[framesInFlight];
		_idxSizes = new ulong[framesInFlight];
		_thumbVtx = new ComPtr<ID3D12Resource>[framesInFlight];
		_thumbVtxSize = new ulong[framesInFlight];
		_thumbIdx = new ComPtr<ID3D12Resource>[framesInFlight];
		_thumbIdxSize = new ulong[framesInFlight];
		_lineVtx = new ComPtr<ID3D12Resource>[framesInFlight];
		_lineVtxSize = new ulong[framesInFlight];
		_lineIdx = new ComPtr<ID3D12Resource>[framesInFlight];
		_lineIdxSize = new ulong[framesInFlight];

		// We index the concatenated vertex buffer per draw-cmd, so advertise VtxOffset support.
		ImGui.GetIO().BackendFlags |= ImGuiBackendFlags.RendererHasVtxOffset;

		CreatePipeline(vertexDxil, fragmentDxil, rtvFormat);
		UploadFontAtlas();
	}

	// The ImGui PSO + root signature. Root signature (matches the imgui.slang reflection: pc → b0, fontTex →
	// t0/s0): a 4-DWORD root constant at b0 (vertex) for the scale+translate push, an SRV table (t0, pixel) for
	// the font atlas, and one static sampler (s0, pixel, linear/wrap). PSO: ImDrawVert input layout, alpha-over
	// blend, no depth, cull none, baked against the backbuffer's (sRGB) RTV format. Mirrors Technique's bake.
	private void CreatePipeline(byte[] vertexDxil, byte[] fragmentDxil, DxgiFormat rtvFormat)
	{
		var srvRange = new DescriptorRange
		{
			RangeType = DescriptorRangeType.Srv,
			NumDescriptors = 1,
			BaseShaderRegister = 0,
			RegisterSpace = 0,
			OffsetInDescriptorsFromTableStart = 0,
		};
		var parameters = stackalloc RootParameter[2];
		parameters[0] = new RootParameter
		{
			ParameterType = RootParameterType.Type32BitConstants,
			ShaderVisibility = ShaderVisibility.Vertex,
			Anonymous = new RootParameterUnion { Constants = new RootConstants { ShaderRegister = 0, RegisterSpace = 0, Num32BitValues = PushCount } },
		};
		parameters[1] = new RootParameter
		{
			ParameterType = RootParameterType.TypeDescriptorTable,
			ShaderVisibility = ShaderVisibility.Pixel,
			Anonymous = new RootParameterUnion { DescriptorTable = new RootDescriptorTable { NumDescriptorRanges = 1, PDescriptorRanges = &srvRange } },
		};
		var sampler = new StaticSamplerDesc
		{
			Filter = Filter.MinMagMipLinear,
			AddressU = TextureAddressMode.Wrap,
			AddressV = TextureAddressMode.Wrap,
			AddressW = TextureAddressMode.Wrap,
			ComparisonFunc = ComparisonFunc.Never,
			BorderColor = StaticBorderColor.OpaqueBlack,
			MinLOD = 0f,
			MaxLOD = float.MaxValue,
			ShaderRegister = 0,
			RegisterSpace = 0,
			ShaderVisibility = ShaderVisibility.Pixel,
		};
		var rootDesc = new RootSignatureDesc
		{
			NumParameters = 2,
			PParameters = parameters,
			NumStaticSamplers = 1,
			PStaticSamplers = &sampler,
			Flags = RootSignatureFlags.AllowInputAssemblerInputLayout,
		};
		ComPtr<ID3D10Blob> blob = default;
		ComPtr<ID3D10Blob> error = default;
		SilkMarshal.ThrowHResult(_ctx.D3D12.SerializeRootSignature(&rootDesc, D3DRootSignatureVersion.Version1, ref blob, ref error));
		SilkMarshal.ThrowHResult(_ctx.Device.CreateRootSignature(0, blob.GetBufferPointer(), blob.GetBufferSize(), out _rootSignature));
		blob.Dispose();
		if (error.Handle is not null)
			error.Dispose();

		// Canonical ImGui blend: src-alpha over for colour, and accumulate coverage into alpha (matches the
		// Vulkan renderer's blend attachment exactly).
		var blend = new BlendDesc { AlphaToCoverageEnable = false, IndependentBlendEnable = false };
		blend.RenderTarget[0] = new RenderTargetBlendDesc
		{
			BlendEnable = true,
			SrcBlend = Blend.SrcAlpha,
			DestBlend = Blend.InvSrcAlpha,
			BlendOp = BlendOp.Add,
			SrcBlendAlpha = Blend.One,
			DestBlendAlpha = Blend.InvSrcAlpha,
			BlendOpAlpha = BlendOp.Add,
			LogicOpEnable = false,
			LogicOp = LogicOp.Noop,
			RenderTargetWriteMask = 0xF,
		};

		// ImDrawVert: pos (float2 @0), uv (float2 @8), col (R8G8B8A8_UNORM @16) — stride 20. The COLOR input is
		// the packed RGBA8 ImGui writes (R in byte 0), decoded to [0,1] RGBA by the UNORM format (matches Vulkan).
		ReadOnlySpan<byte> position = "POSITION\0"u8;
		ReadOnlySpan<byte> texcoord = "TEXCOORD\0"u8;
		ReadOnlySpan<byte> color = "COLOR\0"u8;
		fixed (byte* pVs = vertexDxil)
		fixed (byte* pPs = fragmentDxil)
		fixed (byte* pPos = position)
		fixed (byte* pTex = texcoord)
		fixed (byte* pCol = color)
		{
			var elements = stackalloc InputElementDesc[3];
			elements[0] = new InputElementDesc { SemanticName = pPos, SemanticIndex = 0, Format = DxgiFormat.FormatR32G32Float, InputSlot = 0, AlignedByteOffset = 0, InputSlotClass = InputClassification.PerVertexData, InstanceDataStepRate = 0 };
			elements[1] = new InputElementDesc { SemanticName = pTex, SemanticIndex = 0, Format = DxgiFormat.FormatR32G32Float, InputSlot = 0, AlignedByteOffset = 8, InputSlotClass = InputClassification.PerVertexData, InstanceDataStepRate = 0 };
			elements[2] = new InputElementDesc { SemanticName = pCol, SemanticIndex = 0, Format = DxgiFormat.FormatR8G8B8A8Unorm, InputSlot = 0, AlignedByteOffset = 16, InputSlotClass = InputClassification.PerVertexData, InstanceDataStepRate = 0 };

			var pso = new GraphicsPipelineStateDesc
			{
				PRootSignature = _rootSignature.Handle,
				VS = new ShaderBytecode { PShaderBytecode = pVs, BytecodeLength = (nuint)vertexDxil.Length },
				PS = new ShaderBytecode { PShaderBytecode = pPs, BytecodeLength = (nuint)fragmentDxil.Length },
				BlendState = blend,
				SampleMask = uint.MaxValue,
				RasterizerState = new RasterizerDesc { FillMode = FillMode.Solid, CullMode = CullMode.None, FrontCounterClockwise = false, DepthClipEnable = true },
				DepthStencilState = new DepthStencilDesc { DepthEnable = false, DepthWriteMask = DepthWriteMask.Zero, StencilEnable = false },
				DSVFormat = DxgiFormat.FormatUnknown,
				InputLayout = new InputLayoutDesc { NumElements = 3, PInputElementDescs = elements },
				PrimitiveTopologyType = PrimitiveTopologyType.Triangle,
				NumRenderTargets = 1,
				SampleDesc = new Silk.NET.DXGI.SampleDesc { Count = 1, Quality = 0 },
				Flags = PipelineStateFlags.None,
			};
			pso.RTVFormats[0] = rtvFormat;
			SilkMarshal.ThrowHResult(_ctx.Device.CreateGraphicsPipelineState(&pso, out _pipeline));
			_ctx.SetObjectName((ID3D12Object*)_pipeline.Handle, "pipeline:imgui");
		}
	}

	// Build ImGui's font atlas, upload it to a DEFAULT-heap texture via an UPLOAD-heap staging buffer (a texture
	// can't live in an UPLOAD heap), and leave it in PIXEL_SHADER_RESOURCE. A one-shot command list + fence does
	// the copy at init (the DX12 analogue of the Vulkan renderer's BeginOneTime/EndOneTime).
	private void UploadFontAtlas()
	{
		var io = ImGui.GetIO();
		io.Fonts.GetTexDataAsRGBA32(out nint pixels, out var width, out var height, out var _);

		var texDesc = new ResourceDesc
		{
			Dimension = ResourceDimension.Texture2D,
			Alignment = 0,
			Width = (ulong)width,
			Height = (uint)height,
			DepthOrArraySize = 1,
			MipLevels = 1,
			Format = DxgiFormat.FormatR8G8B8A8Unorm,
			SampleDesc = new Silk.NET.DXGI.SampleDesc { Count = 1, Quality = 0 },
			Layout = TextureLayout.LayoutUnknown,
			Flags = ResourceFlags.None,
		};
		var defaultHeap = new HeapProperties { Type = HeapType.Default, CPUPageProperty = CpuPageProperty.Unknown, MemoryPoolPreference = MemoryPool.Unknown, CreationNodeMask = 1, VisibleNodeMask = 1 };
		SilkMarshal.ThrowHResult(_ctx.Device.CreateCommittedResource(
			&defaultHeap, HeapFlags.None, &texDesc, ResourceStates.CopyDest, (ClearValue*)null, out _fontTexture));

		// The 256-aligned upload layout for subresource 0 (D3D12_TEXTURE_DATA_PITCH_ALIGNMENT).
		PlacedSubresourceFootprint footprint;
		uint numRows;
		ulong rowSizeBytes, totalBytes;
		_ctx.Device.GetCopyableFootprints(&texDesc, 0, 1, 0, &footprint, &numRows, &rowSizeBytes, &totalBytes);

		var uploadHeap = new HeapProperties { Type = HeapType.Upload, CPUPageProperty = CpuPageProperty.Unknown, MemoryPoolPreference = MemoryPool.Unknown, CreationNodeMask = 1, VisibleNodeMask = 1 };
		var uploadDesc = new ResourceDesc
		{
			Dimension = ResourceDimension.Buffer,
			Alignment = 0,
			Width = totalBytes,
			Height = 1,
			DepthOrArraySize = 1,
			MipLevels = 1,
			Format = DxgiFormat.FormatUnknown,
			SampleDesc = new Silk.NET.DXGI.SampleDesc { Count = 1, Quality = 0 },
			Layout = TextureLayout.LayoutRowMajor,
			Flags = ResourceFlags.None,
		};
		ComPtr<ID3D12Resource> staging = default;
		SilkMarshal.ThrowHResult(_ctx.Device.CreateCommittedResource(
			&uploadHeap, HeapFlags.None, &uploadDesc, ResourceStates.GenericRead, (ClearValue*)null, out staging));

		// Copy the atlas into the staging buffer row by row (the dst pitch is 256-aligned; the src is tight width*4).
		void* mapped;
		var noRead = new Silk.NET.Direct3D12.Range { Begin = 0, End = 0 };
		SilkMarshal.ThrowHResult(staging.Map(0, &noRead, &mapped));
		var dstPitch = footprint.Footprint.RowPitch;
		var srcPitch = (uint)width * 4u;
		var src = (byte*)pixels;
		var dst = (byte*)mapped;
		for (var y = 0; y < height; y++)
			System.Buffer.MemoryCopy(src + (long)y * srcPitch, dst + (long)y * dstPitch, dstPitch, srcPitch);
		staging.Unmap(0, (Silk.NET.Direct3D12.Range*)null);

		// Record + execute the buffer→texture copy on a one-shot command list, then transition to a shader resource.
		SilkMarshal.ThrowHResult(_ctx.Device.CreateCommandAllocator(CommandListType.Direct, out ComPtr<ID3D12CommandAllocator> allocator));
		SilkMarshal.ThrowHResult(_ctx.Device.CreateCommandList(0, CommandListType.Direct, allocator, default(ComPtr<ID3D12PipelineState>), out ComPtr<ID3D12GraphicsCommandList> cmd));

		var dstLoc = new TextureCopyLocation
		{
			PResource = _fontTexture.Handle,
			Type = TextureCopyType.SubresourceIndex,
			Anonymous = new TextureCopyLocationUnion { SubresourceIndex = 0 },
		};
		var srcLoc = new TextureCopyLocation
		{
			PResource = staging.Handle,
			Type = TextureCopyType.PlacedFootprint,
			Anonymous = new TextureCopyLocationUnion { PlacedFootprint = footprint },
		};
		cmd.CopyTextureRegion(&dstLoc, 0, 0, 0, &srcLoc, (Box*)null);

		var barrier = new ResourceBarrier
		{
			Type = ResourceBarrierType.Transition,
			Flags = ResourceBarrierFlags.None,
			Anonymous = new ResourceBarrierUnion
			{
				Transition = new ResourceTransitionBarrier
				{
					PResource = _fontTexture.Handle,
					Subresource = 0xffffffff,
					StateBefore = ResourceStates.CopyDest,
					StateAfter = ResourceStates.PixelShaderResource,
				}
			}
		};
		cmd.ResourceBarrier(1, &barrier);
		SilkMarshal.ThrowHResult(cmd.Close());

		var list = (ID3D12CommandList*)cmd.Handle;
		_ctx.GraphicsQueue.ExecuteCommandLists(1, &list);

		// Block until the upload completes (one-time init), then release the staging + one-shot objects.
		SilkMarshal.ThrowHResult(_ctx.Device.CreateFence(0, FenceFlags.None, out ComPtr<ID3D12Fence> fence));
		var ev = CreateEventW(0, 0, 0, 0);
		SilkMarshal.ThrowHResult(_ctx.GraphicsQueue.Signal(fence, 1));
		SilkMarshal.ThrowHResult(fence.SetEventOnCompletion(1, (void*)ev));
		WaitForSingleObject(ev, 0xFFFFFFFF);
		CloseHandle(ev);

		fence.Dispose();
		cmd.Dispose();
		allocator.Dispose();
		staging.Dispose();

		io.Fonts.SetTexID((nint)1); // non-null id; we always bind the one font atlas
		_whiteUv = io.Fonts.TexUvWhitePixel; // a field read — safe on win-arm64 (unlike by-value ImVec2 args)
	}

	/// <summary>
	/// Record the ImGui draw data into the (already-open) hud-pass scope. <paramref name="drawDataPtr"/> is the
	/// native <c>ImDrawData*</c> the host produced via <c>ImGui.Render()</c>; null/empty means no UI. The engine
	/// has already bound the backbuffer RTV; this records the geometry, PSO, and the scissored draw loop.
	/// </summary>
	public void Render(ComPtr<ID3D12GraphicsCommandList> cmd, nint drawDataPtr, uint fbWidth, uint fbHeight, uint frameSlot)
	{
		if (drawDataPtr == nint.Zero)
			return;
		var drawData = new ImDrawDataPtr((ImDrawData*)drawDataPtr);
		if (drawData.CmdListsCount == 0 || drawData.TotalVtxCount == 0 || drawData.DisplaySize.X <= 0 || drawData.DisplaySize.Y <= 0)
			return;

		var vtxStride = (uint)sizeof(ImDrawVert);
		var vtxBytes = (ulong)((long)drawData.TotalVtxCount * vtxStride);
		var idxBytes = (ulong)((long)drawData.TotalIdxCount * sizeof(ushort));
		EnsureBuffer(ref _vtxBuffers[frameSlot], ref _vtxSizes[frameSlot], vtxBytes);
		EnsureBuffer(ref _idxBuffers[frameSlot], ref _idxSizes[frameSlot], idxBytes);

		// Concatenate every cmd-list's vertices/indices into the slot's UPLOAD-heap buffers.
		void* vtxMappedPtr;
		void* idxMappedPtr;
		var noRead = new Silk.NET.Direct3D12.Range { Begin = 0, End = 0 };
		SilkMarshal.ThrowHResult(_vtxBuffers[frameSlot].Map(0, &noRead, &vtxMappedPtr));
		SilkMarshal.ThrowHResult(_idxBuffers[frameSlot].Map(0, &noRead, &idxMappedPtr));
		var vtxDst = (byte*)vtxMappedPtr;
		var idxDst = (byte*)idxMappedPtr;
		long vtxOffset = 0;
		long idxOffset = 0;
		for (var n = 0; n < drawData.CmdListsCount; n++)
		{
			var cmdList = drawData.CmdLists[n];
			var vtxSrcBytes = (long)cmdList.VtxBuffer.Size * vtxStride;
			var idxSrcBytes = (long)cmdList.IdxBuffer.Size * sizeof(ushort);
			System.Buffer.MemoryCopy((void*)cmdList.VtxBuffer.Data, vtxDst + vtxOffset, (long)vtxBytes - vtxOffset, vtxSrcBytes);
			System.Buffer.MemoryCopy((void*)cmdList.IdxBuffer.Data, idxDst + idxOffset, (long)idxBytes - idxOffset, idxSrcBytes);
			vtxOffset += vtxSrcBytes;
			idxOffset += idxSrcBytes;
		}
		_vtxBuffers[frameSlot].Unmap(0, (Silk.NET.Direct3D12.Range*)null);
		_idxBuffers[frameSlot].Unmap(0, (Silk.NET.Direct3D12.Range*)null);

		cmd.SetGraphicsRootSignature(_rootSignature);
		cmd.SetPipelineState(_pipeline);

		// The font SRV: write it into a fresh ring slot (the shader-visible heap the engine bound this frame) and
		// bind the table. ImGui only ever uses the one font atlas, so a single descriptor suffices.
		var (cpu, gpu) = _ring.Allocate(1);
		var srvDesc = new ShaderResourceViewDesc
		{
			Format = DxgiFormat.FormatR8G8B8A8Unorm,
			ViewDimension = SrvDimension.Texture2D,
			Shader4ComponentMapping = 0x1688, // D3D12_DEFAULT_SHADER_4_COMPONENT_MAPPING
			Anonymous = new ShaderResourceViewDescUnion { Texture2D = new Tex2DSrv { MostDetailedMip = 0, MipLevels = 1, PlaneSlice = 0, ResourceMinLODClamp = 0f } },
		};
		_ctx.Device.CreateShaderResourceView(_fontTexture, &srvDesc, cpu);
		cmd.SetGraphicsRootDescriptorTable(1, gpu); // the UI uses only the font atlas; thumbnails draw separately

		var vbv = new VertexBufferView { BufferLocation = _vtxBuffers[frameSlot].GetGPUVirtualAddress(), SizeInBytes = (uint)vtxBytes, StrideInBytes = vtxStride };
		var ibv = new IndexBufferView { BufferLocation = _idxBuffers[frameSlot].GetGPUVirtualAddress(), SizeInBytes = (uint)idxBytes, Format = DxgiFormat.FormatR16Uint };
		cmd.IASetPrimitiveTopology(D3DPrimitiveTopology.D3DPrimitiveTopologyTrianglelist);
		cmd.IASetVertexBuffers(0, 1, &vbv);
		cmd.IASetIndexBuffer(&ibv);

		// Negative-height viewport (origin at the bottom): turns D3D12's (1-ndc.y) NDC→screen map into Vulkan's
		// (ndc.y+1), matching the rest of the backend's clip space — so the SAME push as the Vulkan renderer (no
		// Y-flip: ImGui's +Y-down pixel origin → clip y=-1 at the top → screen top) places the UI right-side up.
		var viewport = new Viewport { TopLeftX = 0, TopLeftY = fbHeight, Width = fbWidth, Height = -(float)fbHeight, MinDepth = 0, MaxDepth = 1 };
		cmd.RSSetViewports(1, &viewport);

		var displayPos = drawData.DisplayPos;
		var displaySize = drawData.DisplaySize;
		var push = stackalloc float[4];
		push[0] = 2.0f / displaySize.X;
		push[1] = 2.0f / displaySize.Y;
		push[2] = -1.0f - displayPos.X * push[0];
		push[3] = -1.0f - displayPos.Y * push[1];
		cmd.SetGraphicsRoot32BitConstants(0, PushCount, push, 0);

		var clipOff = displayPos;
		var clipScale = drawData.FramebufferScale;
		if (clipScale.X <= 0 || clipScale.Y <= 0)
			clipScale = Vector2.One; // host didn't set DisplayFramebufferScale — assume 1:1
		var globalVtx = 0;
		var globalIdx = 0;
		for (var n = 0; n < drawData.CmdListsCount; n++)
		{
			var cmdList = drawData.CmdLists[n];
			for (var c = 0; c < cmdList.CmdBuffer.Size; c++)
			{
				var pcmd = cmdList.CmdBuffer[c];
				var minX = (pcmd.ClipRect.X - clipOff.X) * clipScale.X;
				var minY = (pcmd.ClipRect.Y - clipOff.Y) * clipScale.Y;
				var maxX = (pcmd.ClipRect.Z - clipOff.X) * clipScale.X;
				var maxY = (pcmd.ClipRect.W - clipOff.Y) * clipScale.Y;
				if (minX < 0) minX = 0;
				if (minY < 0) minY = 0;
				if (maxX > fbWidth) maxX = fbWidth;
				if (maxY > fbHeight) maxY = fbHeight;
				if (maxX <= minX || maxY <= minY)
					continue;

				// Scissor stays in pixel space (top-left origin) regardless of the viewport sign.
				var scissor = new Silk.NET.Maths.Box2D<int>((int)minX, (int)minY, (int)maxX, (int)maxY);
				cmd.RSSetScissorRects(1, &scissor);
				cmd.DrawIndexedInstanced(pcmd.ElemCount, 1, pcmd.IdxOffset + (uint)globalIdx, (int)(pcmd.VtxOffset + (uint)globalVtx), 0);
			}
			globalIdx += cmdList.IdxBuffer.Size;
			globalVtx += cmdList.VtxBuffer.Size;
		}
	}

	// Grow (or first-create) an UPLOAD-heap (GENERIC_READ) buffer to hold at least <paramref name="needed"/>
	// bytes. The slot's fence has been waited before this slot records again, so releasing/replacing the buffer
	// can't race the GPU.
	private void EnsureBuffer(ref ComPtr<ID3D12Resource> buffer, ref ulong currentSize, ulong needed)
	{
		if (needed == 0)
			needed = 256;
		if (buffer.Handle is not null && currentSize >= needed)
			return;
		if (buffer.Handle is not null)
			buffer.Dispose();

		var grown = needed + needed / 2; // headroom so a frame's growth doesn't reallocate next frame
		var heapProps = new HeapProperties { Type = HeapType.Upload, CPUPageProperty = CpuPageProperty.Unknown, MemoryPoolPreference = MemoryPool.Unknown, CreationNodeMask = 1, VisibleNodeMask = 1 };
		var desc = new ResourceDesc
		{
			Dimension = ResourceDimension.Buffer,
			Alignment = 0,
			Width = grown,
			Height = 1,
			DepthOrArraySize = 1,
			MipLevels = 1,
			Format = DxgiFormat.FormatUnknown,
			SampleDesc = new Silk.NET.DXGI.SampleDesc { Count = 1, Quality = 0 },
			Layout = TextureLayout.LayoutRowMajor,
			Flags = ResourceFlags.None,
		};
		SilkMarshal.ThrowHResult(_ctx.Device.CreateCommittedResource(
			&heapProps, HeapFlags.None, &desc, ResourceStates.GenericRead, (ClearValue*)null, out buffer));
		currentSize = grown;
	}

	public void Dispose()
	{
		if (_pipeline.Handle is not null)
			_pipeline.Dispose();
		if (_rootSignature.Handle is not null)
			_rootSignature.Dispose();
		if (_fontTexture.Handle is not null)
			_fontTexture.Dispose();
		for (var i = 0; i < _framesInFlight; i++)
		{
			if (_vtxBuffers[i].Handle is not null)
				_vtxBuffers[i].Dispose();
			if (_idxBuffers[i].Handle is not null)
				_idxBuffers[i].Dispose();
			if (_thumbVtx[i].Handle is not null)
				_thumbVtx[i].Dispose();
			if (_thumbIdx[i].Handle is not null)
				_thumbIdx[i].Dispose();
			if (_lineVtx[i].Handle is not null)
				_lineVtx[i].Dispose();
			if (_lineIdx[i].Handle is not null)
				_lineIdx[i].Dispose();
		}
	}

	[DllImport("kernel32", SetLastError = true)]
	private static extern nint CreateEventW(nint lpEventAttributes, int bManualReset, int bInitialState, nint lpName);

	[DllImport("kernel32", SetLastError = true)]
	private static extern uint WaitForSingleObject(nint hHandle, uint dwMilliseconds);

	[DllImport("kernel32", SetLastError = true)]
	private static extern int CloseHandle(nint hObject);
}
#endif
