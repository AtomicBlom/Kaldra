using System.Runtime.InteropServices;

using Loom.Core;

using Silk.NET.Core.Native;
using Silk.NET.Direct3D12;

using DxgiFormat = Silk.NET.DXGI.Format;

namespace Loom.DX12;

/// <summary>
/// The DX12 backend's game-UI renderer (PlanningUI.md §D6) — the twin of <see cref="DX12ImGuiRenderer"/> for the
/// XAML UI engine and the DX12 counterpart of <c>Loom.Vulkan.UIRenderer</c>, cloned from the ImGui renderer. The
/// "ui" pass's <c>ctx.DrawUI(surface)</c> forwards that surface's composited, neutral <see cref="UIDrawData"/>
/// here; this records it into the already-open render-target scope (the engine bound the backbuffer RTV + the
/// negative-height viewport already).
/// <para>
/// Differences from the ImGui twin: the input layout is <see cref="UIVertex"/> (premultiplied RGBA16F colour +
/// per-prim params), so the PSO blends <b>premultiplied</b> alpha-over; one <c>ui.slang</c> PSO draws every
/// primitive family, the <see cref="UIDrawCommand.Kind"/> selected per command via a root constant; and a small
/// texture table (a 1×1 white texel at <see cref="UIDrawCommand.TextureId"/> 0, the MTSDF font atlas at 1) is
/// bound per command (a fresh SRV written into the frame's descriptor ring). Per-slot vertex/index buffers are
/// UPLOAD-heap and grown on demand.
/// </para>
/// </summary>
internal sealed unsafe class DX12UIRenderer : IDisposable
{
	private readonly DX12Context _ctx;
	private readonly DX12DescriptorRing _ring;

	private ComPtr<ID3D12RootSignature> _rootSignature;
	private ComPtr<ID3D12PipelineState> _pipeline;

	// Texture table: index = UIDrawCommand.TextureId. 0 = a 1×1 white texel (solid fills/rects); 1 = the MTSDF
	// font atlas page (glyphs). Each a DEFAULT-heap texture left in PIXEL_SHADER_RESOURCE.
	private readonly ComPtr<ID3D12Resource>[] _texResources;
	private readonly int _texCount;

	private readonly int _framesInFlight;
	private readonly ComPtr<ID3D12Resource>[] _vtxBuffers;
	private readonly ulong[] _vtxSizes;
	private readonly ComPtr<ID3D12Resource>[] _idxBuffers;
	private readonly ulong[] _idxSizes;

	// Push block (ui.slang PushConstants): scale.xy, translate.xy, kind (uint), paperWhite, isHdr (uint) = 7 DWORDs at b0.
	private const uint PushCount = 7;
	private const float HdrPaperWhiteScale = 200f / 80f; // UIPaperWhiteNits / 80 (§D4.4); SDR passes 1.0.

	public DX12UIRenderer(DX12Context ctx, DX12DescriptorRing ring, byte[] vertexDxil, byte[] fragmentDxil, DxgiFormat rtvFormat, int framesInFlight, TextureData? fontAtlas)
	{
		_ctx = ctx;
		_ring = ring;
		_framesInFlight = framesInFlight;
		_vtxBuffers = new ComPtr<ID3D12Resource>[framesInFlight];
		_vtxSizes = new ulong[framesInFlight];
		_idxBuffers = new ComPtr<ID3D12Resource>[framesInFlight];
		_idxSizes = new ulong[framesInFlight];

		_texCount = fontAtlas is null ? 1 : 2;
		_texResources = new ComPtr<ID3D12Resource>[_texCount];

		CreatePipeline(vertexDxil, fragmentDxil, rtvFormat);

		_texResources[0] = UploadTexture(1, 1, [255, 255, 255, 255]);
		if (fontAtlas is not null)
		{
			var level = fontAtlas.Levels[0];
			_texResources[1] = UploadTexture(level.Width, level.Height, level.Pixels);
		}
	}

	// The ui.slang PSO + root signature (matches ui.slang's reflection: pc → b0 visible to both stages, tex → t0/s0).
	// Root param 0 = 7-DWORD root constant at b0 (the push); param 1 = an SRV table (t0, pixel) for the bound texture;
	// one static sampler (s0, pixel, linear/CLAMP). PSO: UIVertex input layout, PREMULTIPLIED alpha-over blend, no
	// depth, cull none, baked against the backbuffer's RTV format.
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
			ShaderVisibility = ShaderVisibility.All, // scale/translate read in vertex; kind/paperWhite/isHdr in pixel
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
			AddressU = TextureAddressMode.Clamp,
			AddressV = TextureAddressMode.Clamp,
			AddressW = TextureAddressMode.Clamp,
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

		// Premultiplied alpha-over: the compositor folds opacity into the (premultiplied) vertex colour, so the
		// source factor is ONE (not SRC_ALPHA) — matches the Vulkan renderer's blend.
		var blend = new BlendDesc { AlphaToCoverageEnable = false, IndependentBlendEnable = false };
		blend.RenderTarget[0] = new RenderTargetBlendDesc
		{
			BlendEnable = true,
			SrcBlend = Blend.One,
			DestBlend = Blend.InvSrcAlpha,
			BlendOp = BlendOp.Add,
			SrcBlendAlpha = Blend.One,
			DestBlendAlpha = Blend.InvSrcAlpha,
			BlendOpAlpha = BlendOp.Add,
			LogicOpEnable = false,
			LogicOp = LogicOp.Noop,
			RenderTargetWriteMask = 0xF,
		};

		// UIVertex: position (float2 @0), uv (float2 @8), colour (half4 @16), params (float4 @24) — stride 40.
		ReadOnlySpan<byte> position = "POSITION\0"u8;
		ReadOnlySpan<byte> texcoord = "TEXCOORD\0"u8;
		ReadOnlySpan<byte> color = "COLOR\0"u8;
		fixed (byte* pVs = vertexDxil)
		fixed (byte* pPs = fragmentDxil)
		fixed (byte* pPos = position)
		fixed (byte* pTex = texcoord)
		fixed (byte* pCol = color)
		{
			var elements = stackalloc InputElementDesc[4];
			elements[0] = new InputElementDesc { SemanticName = pPos, SemanticIndex = 0, Format = DxgiFormat.FormatR32G32Float, InputSlot = 0, AlignedByteOffset = 0, InputSlotClass = InputClassification.PerVertexData, InstanceDataStepRate = 0 };
			elements[1] = new InputElementDesc { SemanticName = pTex, SemanticIndex = 0, Format = DxgiFormat.FormatR32G32Float, InputSlot = 0, AlignedByteOffset = 8, InputSlotClass = InputClassification.PerVertexData, InstanceDataStepRate = 0 };
			elements[2] = new InputElementDesc { SemanticName = pCol, SemanticIndex = 0, Format = DxgiFormat.FormatR16G16B16A16Float, InputSlot = 0, AlignedByteOffset = 16, InputSlotClass = InputClassification.PerVertexData, InstanceDataStepRate = 0 };
			elements[3] = new InputElementDesc { SemanticName = pTex, SemanticIndex = 1, Format = DxgiFormat.FormatR32G32B32A32Float, InputSlot = 0, AlignedByteOffset = 24, InputSlotClass = InputClassification.PerVertexData, InstanceDataStepRate = 0 };

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
				InputLayout = new InputLayoutDesc { NumElements = 4, PInputElementDescs = elements },
				PrimitiveTopologyType = PrimitiveTopologyType.Triangle,
				NumRenderTargets = 1,
				SampleDesc = new Silk.NET.DXGI.SampleDesc { Count = 1, Quality = 0 },
				Flags = PipelineStateFlags.None,
			};
			pso.RTVFormats[0] = rtvFormat;
			SilkMarshal.ThrowHResult(_ctx.Device.CreateGraphicsPipelineState(&pso, out _pipeline));
			_ctx.SetObjectName((ID3D12Object*)_pipeline.Handle, "pipeline:ui");
		}
	}

	// Upload tightly packed RGBA8 pixels to a DEFAULT-heap texture via an UPLOAD-heap staging buffer, leaving it in
	// PIXEL_SHADER_RESOURCE. A one-shot command list + fence does the copy at init (mirrors DX12ImGuiRenderer's
	// UploadFontAtlas). Linear/UNORM — the atlas carries SDF distances, never sRGB colour.
	private ComPtr<ID3D12Resource> UploadTexture(int width, int height, ReadOnlySpan<byte> rgba)
	{
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
			&defaultHeap, HeapFlags.None, &texDesc, ResourceStates.CopyDest, (ClearValue*)null, out ComPtr<ID3D12Resource> texture));

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
		SilkMarshal.ThrowHResult(_ctx.Device.CreateCommittedResource(
			&uploadHeap, HeapFlags.None, &uploadDesc, ResourceStates.GenericRead, (ClearValue*)null, out ComPtr<ID3D12Resource> staging));

		void* mapped;
		var noRead = new Silk.NET.Direct3D12.Range { Begin = 0, End = 0 };
		SilkMarshal.ThrowHResult(staging.Map(0, &noRead, &mapped));
		var dstPitch = footprint.Footprint.RowPitch;
		var srcPitch = (uint)width * 4u;
		fixed (byte* src = rgba)
		{
			var dst = (byte*)mapped;
			for (var y = 0; y < height; y++)
				System.Buffer.MemoryCopy(src + (long)y * srcPitch, dst + (long)y * dstPitch, dstPitch, srcPitch);
		}
		staging.Unmap(0, (Silk.NET.Direct3D12.Range*)null);

		SilkMarshal.ThrowHResult(_ctx.Device.CreateCommandAllocator(CommandListType.Direct, out ComPtr<ID3D12CommandAllocator> allocator));
		SilkMarshal.ThrowHResult(_ctx.Device.CreateCommandList(0, CommandListType.Direct, allocator, default(ComPtr<ID3D12PipelineState>), out ComPtr<ID3D12GraphicsCommandList> cmd));

		var dstLoc = new TextureCopyLocation
		{
			PResource = texture.Handle,
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
					PResource = texture.Handle,
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

		return texture;
	}

	/// <summary>
	/// Record one surface's composited <see cref="UIDrawData"/> into the (already-open) "ui" pass scope. The engine
	/// has bound the backbuffer RTV; this records geometry, PSO, and the scissored draw loop. Empty data is a no-op.
	/// </summary>
	public void Render(ComPtr<ID3D12GraphicsCommandList> cmd, UIDrawData draw, uint fbWidth, uint fbHeight, uint frameSlot)
	{
		var vertices = draw.Vertices.Span;
		var indices = draw.Indices.Span;
		var commands = draw.Commands.Span;
		if (vertices.Length == 0 || indices.Length == 0 || commands.Length == 0)
			return;
		if (draw.SurfaceSize.X <= 0 || draw.SurfaceSize.Y <= 0)
			return;

		var vtxStride = (uint)sizeof(UIVertex);
		var vtxBytes = (ulong)(vertices.Length * sizeof(UIVertex));
		var idxBytes = (ulong)(indices.Length * sizeof(ushort));
		EnsureBuffer(ref _vtxBuffers[frameSlot], ref _vtxSizes[frameSlot], vtxBytes);
		EnsureBuffer(ref _idxBuffers[frameSlot], ref _idxSizes[frameSlot], idxBytes);

		void* vtxMapped;
		void* idxMapped;
		var noRead = new Silk.NET.Direct3D12.Range { Begin = 0, End = 0 };
		SilkMarshal.ThrowHResult(_vtxBuffers[frameSlot].Map(0, &noRead, &vtxMapped));
		MemoryMarshal.AsBytes(vertices).CopyTo(new Span<byte>(vtxMapped, (int)vtxBytes));
		_vtxBuffers[frameSlot].Unmap(0, (Silk.NET.Direct3D12.Range*)null);
		SilkMarshal.ThrowHResult(_idxBuffers[frameSlot].Map(0, &noRead, &idxMapped));
		MemoryMarshal.AsBytes(indices).CopyTo(new Span<byte>(idxMapped, (int)idxBytes));
		_idxBuffers[frameSlot].Unmap(0, (Silk.NET.Direct3D12.Range*)null);

		cmd.SetGraphicsRootSignature(_rootSignature);
		cmd.SetPipelineState(_pipeline);

		var vbv = new VertexBufferView { BufferLocation = _vtxBuffers[frameSlot].GetGPUVirtualAddress(), SizeInBytes = (uint)vtxBytes, StrideInBytes = vtxStride };
		var ibv = new IndexBufferView { BufferLocation = _idxBuffers[frameSlot].GetGPUVirtualAddress(), SizeInBytes = (uint)idxBytes, Format = DxgiFormat.FormatR16Uint };
		cmd.IASetPrimitiveTopology(D3DPrimitiveTopology.D3DPrimitiveTopologyTrianglelist);
		cmd.IASetVertexBuffers(0, 1, &vbv);
		cmd.IASetIndexBuffer(&ibv);

		// Negative-height viewport (origin at the bottom) so the same +Y-down clip math as the Vulkan renderer applies.
		var viewport = new Viewport { TopLeftX = 0, TopLeftY = fbHeight, Width = fbWidth, Height = -(float)fbHeight, MinDepth = 0, MaxDepth = 1 };
		cmd.RSSetViewports(1, &viewport);

		var push = stackalloc float[7];
		push[0] = 2.0f / draw.SurfaceSize.X;
		push[1] = 2.0f / draw.SurfaceSize.Y;
		push[2] = -1.0f;
		push[3] = -1.0f;
		push[5] = draw.IsHdrSurface ? HdrPaperWhiteScale : 1.0f;
		*(uint*)(push + 6) = draw.IsHdrSurface ? 1u : 0u;

		foreach (var c in commands)
		{
			var clip = c.ClipRect;
			var minX = MathF.Max(0, clip.X);
			var minY = MathF.Max(0, clip.Y);
			var maxX = MathF.Min(fbWidth, clip.X + clip.Width);
			var maxY = MathF.Min(fbHeight, clip.Y + clip.Height);
			if (maxX <= minX || maxY <= minY)
				continue;

			*(uint*)(push + 4) = (uint)c.Kind;
			cmd.SetGraphicsRoot32BitConstants(0, PushCount, push, 0);

			// Write the bound texture's SRV into a fresh ring slot (the shader-visible heap bound this frame).
			var resource = _texResources[Math.Clamp(c.TextureId, 0, _texCount - 1)];
			var (cpu, gpu) = _ring.Allocate(1);
			var srvDesc = new ShaderResourceViewDesc
			{
				Format = DxgiFormat.FormatR8G8B8A8Unorm,
				ViewDimension = SrvDimension.Texture2D,
				Shader4ComponentMapping = 0x1688, // D3D12_DEFAULT_SHADER_4_COMPONENT_MAPPING
				Anonymous = new ShaderResourceViewDescUnion { Texture2D = new Tex2DSrv { MostDetailedMip = 0, MipLevels = 1, PlaneSlice = 0, ResourceMinLODClamp = 0f } },
			};
			_ctx.Device.CreateShaderResourceView(resource, &srvDesc, cpu);
			cmd.SetGraphicsRootDescriptorTable(1, gpu);

			var scissor = new Silk.NET.Maths.Box2D<int>((int)minX, (int)minY, (int)maxX, (int)maxY);
			cmd.RSSetScissorRects(1, &scissor);
			cmd.DrawIndexedInstanced((uint)c.IndexCount, 1, (uint)c.IndexOffset, c.VertexOffset, 0);
		}
	}

	// Grow (or first-create) an UPLOAD-heap (GENERIC_READ) buffer to hold at least <paramref name="needed"/> bytes.
	// The slot's fence has been waited before this slot records again, so releasing/replacing it can't race the GPU.
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
		for (var i = 0; i < _texCount; i++)
		{
			if (_texResources[i].Handle is not null)
				_texResources[i].Dispose();
		}
		for (var i = 0; i < _framesInFlight; i++)
		{
			if (_vtxBuffers[i].Handle is not null)
				_vtxBuffers[i].Dispose();
			if (_idxBuffers[i].Handle is not null)
				_idxBuffers[i].Dispose();
		}
	}

	[DllImport("kernel32", SetLastError = true)]
	private static extern nint CreateEventW(nint lpEventAttributes, int bManualReset, int bInitialState, nint lpName);

	[DllImport("kernel32", SetLastError = true)]
	private static extern uint WaitForSingleObject(nint hHandle, uint dwMilliseconds);

	[DllImport("kernel32", SetLastError = true)]
	private static extern int CloseHandle(nint hObject);
}
