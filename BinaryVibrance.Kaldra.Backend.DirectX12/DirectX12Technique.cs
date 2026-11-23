using BinaryVibrance.Kaldra.Core.Geometry;
using BinaryVibrance.Kaldra.Core.Graph;
using BinaryVibrance.Kaldra.Core.Materials;
using BinaryVibrance.Kaldra.Graph;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D12;
using Silk.NET.DXGI;
using D3DCullMode = Silk.NET.Direct3D12.CullMode;
using Format = Silk.NET.DXGI.Format;

namespace BinaryVibrance.Kaldra.Backend.DirectX12;

/// <summary>One baked pipeline + its root signature, with the root-parameter slots the context binds against.</summary>
internal sealed unsafe class DirectX12Technique : IDisposable
{
	// 32-bit root-constant count for a draw-list technique's all-stage push block — the per-list scalars plus the
	// per-command tag slot the context rewrites each draw (the UI block: scale, translate, paper-white, hdr, kind).
	private const uint ListPushDwords = 8;

	/// <summary>
	/// Size of a technique's inline scalar root-constant block, the DirectX 12 counterpart of Vulkan's push-constant
	/// range. A push routed here must fit it.
	/// </summary>
	public const int ScalarPushSize = 16 * sizeof(float);

	/// <summary>Size of a draw-list technique's push block, including the trailing per-command tag slot.</summary>
	public const int ListPushSize = (int)ListPushDwords * sizeof(float);

	public ComPtr<ID3D12RootSignature> RootSignature;
	public ComPtr<ID3D12PipelineState> Pipeline;

	/// <summary>True for a compute technique — bound via the compute root signature + <c>SetPipelineState</c> and dispatched.</summary>
	public bool IsCompute { get; }

	/// <summary>Root-parameter slot for the 32-bit root constants (MVP for a mesh/particle, scalar block for a post/compute pass), or -1 if none.</summary>
	public int RootConstantParam { get; private set; } = -1;

	/// <summary>Root-parameter slot for the SRV descriptor table (sampled inputs), or -1 if the technique samples nothing.</summary>
	public int TableParam { get; private set; } = -1;

	/// <summary>Number of sampled inputs the technique binds (the SRV table's descriptor count) — how many textures a material fills.</summary>
	public int InputCount { get; }

	/// <summary>Root-parameter slot for the storage-buffer root UAV (a compute's <c>.ReadWrites</c> of a buffer), or -1 if none.</summary>
	public int UavRootParam { get; private set; } = -1;

	/// <summary>Root-parameter slot of the FIRST read-only structured-buffer input root SRV (skin: bind-pose verts, bone palette); the i-th input SRV is this + i. -1 if the compute reads no input buffers.</summary>
	public int InputBufferBaseParam { get; private set; } = -1;

	/// <summary>Number of read-only structured-buffer input root SRVs the compute binds.</summary>
	public int InputBufferCount { get; }

	/// <summary>Root-parameter slot for the storage-image / structured-buffer UAV descriptor table (a compute's <c>.ReadWrites</c> of a texture or structured buffer), or -1 if none.</summary>
	public int UavTableParam { get; private set; } = -1;

	/// <summary>Root-parameter slot of the FIRST uniform buffer's root CBV (the i-th uniform buffer is this + i, at its
	/// reflected register) — the uniform seam: CSM matrices, lighting params, wind. -1 if the technique reads no constant buffer.</summary>
	public int CbvRootParam { get; private set; } = -1;

	/// <summary>How many uniform buffers (one root CBV each) the technique declares — the reflected count. A pass's <c>.Reads</c> Constants fill the first slots; an in-closure push fills the next.</summary>
	public int UniformBufferCount { get; }

	/// <summary>Element stride (bytes) of a structured-buffer storage resource, for building its UAV descriptor's <c>StructureByteStride</c>/<c>NumElements</c>; 0 for a texture or raw buffer.</summary>
	public int StorageBufferStride { get; private set; }

	/// <summary>Per-instance vertex stride (bytes) from the technique's declared instance layout — the authoritative
	/// stride for binding a GPU-filled instance buffer (instanced mesh data or a particle stream); 0 when the
	/// technique has no per-instance stream.</summary>
	public int InstanceStride { get; }

	/// <summary>Which uniform slot carries the per-material parameter block, or null when the shader declares none.</summary>
	public int? MaterialUniformSlot { get; }

	/// <summary>The shader's reflected bindings, which a pass's <c>.Reads</c> storage buffers are held to at the draw.</summary>
	public ShaderBindingMap Bindings { get; }

	// The on-disk blob cache and this pipeline's key in it. Null cache = this run does not persist pipelines.
	private readonly DirectX12PipelineBlobCache? _blobs;
	private readonly string _blobKey = "";

	/// <summary>
	/// Create a graphics pipeline, offering the driver the blob a previous run compiled and falling back to a real
	/// compile when it refuses it. A refusal is the expected outcome after a driver update — every blob goes stale at
	/// once — so it is a fall-through, not an error.
	/// </summary>
	/// <remarks>
	/// Every object here is created into a local and only then stored. Silk's <c>out ComPtr</c> overloads hand the
	/// driver the raw address of the destination, unpinned: given a field of this (movable) object, a GC on another
	/// thread during a long compile moves it, the driver writes the old address, and the field reads back null with
	/// the call reporting success. A local on the stack never moves.
	/// </remarks>
	private void CreateGraphicsPipeline(DirectX12Context ctx, GraphicsPipelineStateDesc* pso)
	{
		ComPtr<ID3D12PipelineState> made;
		if (_blobs?.TryGet(_blobKey) is { } cached)
		{
			fixed (byte* p = cached)
			{
				pso->CachedPSO = new CachedPipelineState { PCachedBlob = p, CachedBlobSizeInBytes = (nuint)cached.Length };
				if (ctx.Device.CreateGraphicsPipelineState(pso, out made) >= 0)
				{
					Pipeline = made;
					return;
				}
			}
			pso->CachedPSO = default;
		}
		SilkMarshal.ThrowHResult(ctx.Device.CreateGraphicsPipelineState(pso, out made));
		Pipeline = made;
		_blobs?.Capture(_blobKey, Pipeline);
	}

	/// <summary>The compute counterpart of <see cref="CreateGraphicsPipeline"/>.</summary>
	private void CreateComputePipeline(DirectX12Context ctx, ComputePipelineStateDesc* pso)
	{
		ComPtr<ID3D12PipelineState> made;
		if (_blobs?.TryGet(_blobKey) is { } cached)
		{
			fixed (byte* p = cached)
			{
				pso->CachedPSO = new CachedPipelineState { PCachedBlob = p, CachedBlobSizeInBytes = (nuint)cached.Length };
				if (ctx.Device.CreateComputePipelineState(pso, out made) >= 0)
				{
					Pipeline = made;
					return;
				}
			}
			pso->CachedPSO = default;
		}
		SilkMarshal.ThrowHResult(ctx.Device.CreateComputePipelineState(pso, out made));
		Pipeline = made;
		_blobs?.Capture(_blobKey, Pipeline);
	}

	public DirectX12Technique(DirectX12Context ctx, DirectX12TechniqueTemplate template, ReadOnlySpan<Format> colorFormats, Format depthFormat, bool depthReadOnly, DirectX12PipelineBlobCache? blobs = null, string blobKey = "", int samples = 1)
	{
		_blobs = blobs;
		_blobKey = blobKey;
		IsCompute = template.IsCompute;
		StorageBufferStride = template.StorageBufferStride;
		InputCount = template.InputCount;
		InputBufferCount = template.InputBufferCount;
		UniformBufferCount = template.UniformBufferCount;
		InstanceStride = template.Instance?.Stride ?? 0;
		MaterialUniformSlot = template.MaterialUniformSlot;
		Bindings = template.Bindings;

		if (template.IsCompute)
		{
			BakeCompute(ctx, template);
			return;
		}
		BakeGraphics(ctx, template, colorFormats, depthFormat, depthReadOnly, samples);
	}

	// A compute root signature shaped to the technique: a 16-float scalar root constant (always) + a root CBV
	// per constant buffer it declares (the pass's .Reads Constants) + an optional SRV table for sampled inputs (e.g. the auto-exposure compute metering the lit scene) + the
	// storage binding — a RW texture (the 1×1 exposure image) OR a RW STRUCTURED buffer (the GPU-particle stream)
	// via a UAV descriptor TABLE, since both need a real descriptor (a structured buffer's StructureByteStride/
	// NumElements is what makes the shader's GetDimensions return a non-zero count; a root UAV is a raw GPU address
	// with no metadata, so GetDimensions yields 0 and the particle update early-outs every thread). Only a RAW
	// buffer with no reflected stride (a ByteAddressBuffer) keeps the root-UAV-by-address path. No compute technique
	// mixes an image and a buffer. All visible to ShaderVisibility.All (compute isn't covered by the per-stage
	// VERTEX/PIXEL visibilities). A static sampler per sampled input pairs with the SRV table. Every register is the
	// one the DXIL reflection gives the parameter (DirectX12Registers).
	private void BakeCompute(DirectX12Context ctx, DirectX12TechniqueTemplate template)
	{
		// root constants + N uniform CBVs + M read-only structured-buffer root SRVs + optional SRV table + storage.
		var parameters = stackalloc RootParameter[3 + template.UniformBufferCount + template.InputBufferCount];
		var paramCount = 0;
		var registers = template.Registers;
		var uavTableCount = template.StorageImages > 0 ? template.StorageImages : template.StorageBuffers;
		var srvRanges = stackalloc DescriptorRange[Math.Max(1, template.InputCount)];
		FillRanges(srvRanges, DescriptorRangeType.Srv, registers.Textures, template.InputCount);
		var uavRanges = stackalloc DescriptorRange[Math.Max(1, uavTableCount)];
		FillRanges(uavRanges, DescriptorRangeType.Uav, template.StorageImages > 0 ? registers.StorageImages : registers.ReadWriteBuffers, uavTableCount);
		// A structured buffer (stride known from reflection) and a storage image both bind via the UAV table;
		// only a raw buffer (stride 0) falls back to a root UAV.
		var bufferViaTable = template.StorageBuffers > 0 && template.StorageBufferStride > 0;

		RootConstantParam = paramCount;
		parameters[paramCount++] = new RootParameter
		{
			ParameterType = RootParameterType.Type32BitConstants,
			ShaderVisibility = ShaderVisibility.All,
			Anonymous = new RootParameterUnion { Constants = new RootConstants { ShaderRegister = (uint)registers.Push, RegisterSpace = 0, Num32BitValues = 16 } },
		};
		// The pass's .Reads Constants as root CBVs, as a graphics technique takes them, each at its reflected register.
		if (template.UniformBufferCount > 0)
		{
			CbvRootParam = paramCount;
			for (var u = 0; u < template.UniformBufferCount; u++)
				parameters[paramCount++] = new RootParameter
				{
					ParameterType = RootParameterType.TypeCbv,
					ShaderVisibility = ShaderVisibility.All,
					Anonymous = new RootParameterUnion { Descriptor = new RootDescriptor { ShaderRegister = (uint)registers.UniformBuffers[u], RegisterSpace = 0 } },
				};
		}
		// Read-only structured-buffer inputs (skin: bind-pose verts, bone palette; a pass's .Reads Storage) as
		// root SRVs by GPU address — no descriptor heap — each at the t register reflection gives it.
		if (template.InputBufferCount > 0)
		{
			InputBufferBaseParam = paramCount;
			for (var b = 0; b < template.InputBufferCount; b++)
				parameters[paramCount++] = new RootParameter
				{
					ParameterType = RootParameterType.TypeSrv,
					ShaderVisibility = ShaderVisibility.All,
					Anonymous = new RootParameterUnion { Descriptor = new RootDescriptor { ShaderRegister = (uint)registers.InputBuffers[b], RegisterSpace = 0 } },
				};
		}
		if (template.InputCount > 0)
		{
			TableParam = paramCount;
			parameters[paramCount++] = new RootParameter
			{
				ParameterType = RootParameterType.TypeDescriptorTable,
				ShaderVisibility = ShaderVisibility.All,
				Anonymous = new RootParameterUnion { DescriptorTable = new RootDescriptorTable { NumDescriptorRanges = (uint)template.InputCount, PDescriptorRanges = srvRanges } },
			};
		}
		if (template.StorageImages > 0 || bufferViaTable)
		{
			UavTableParam = paramCount;
			parameters[paramCount++] = new RootParameter
			{
				ParameterType = RootParameterType.TypeDescriptorTable,
				ShaderVisibility = ShaderVisibility.All,
				Anonymous = new RootParameterUnion { DescriptorTable = new RootDescriptorTable { NumDescriptorRanges = (uint)uavTableCount, PDescriptorRanges = uavRanges } },
			};
		}
		else if (template.StorageBuffers > 0)
		{
			UavRootParam = paramCount;
			parameters[paramCount++] = new RootParameter
			{
				ParameterType = RootParameterType.TypeUav,
				ShaderVisibility = ShaderVisibility.All,
				Anonymous = new RootParameterUnion { Descriptor = new RootDescriptor { ShaderRegister = (uint)registers.ReadWriteBuffers[0], RegisterSpace = 0 } },
			};
		}

		var samplers = stackalloc StaticSamplerDesc[template.InputCount > 0 ? template.InputCount : 1];
		for (var i = 0; i < template.InputCount; i++)
		{
			// A slot the shader samples by comparison takes a comparison sampler, as a graphics technique's does
			var compare = template.ComparedInputs is { } c && i < c.Length && c[i];
			samplers[i] = new StaticSamplerDesc
			{
				Filter = compare ? Filter.ComparisonMinMagLinearMipPoint : template.LinearFilter ? Filter.MinMagMipLinear : Filter.MinMagMipPoint,
				AddressU = compare ? TextureAddressMode.Clamp : ToTextureAddress(template.Address),
				AddressV = compare ? TextureAddressMode.Clamp : ToTextureAddress(template.Address),
				AddressW = compare ? TextureAddressMode.Clamp : ToTextureAddress(template.Address),
				ComparisonFunc = compare ? ToD3DCompare(template.SampleCompare!.Value) : ComparisonFunc.Never,
				BorderColor = StaticBorderColor.OpaqueBlack,
				MinLOD = 0f,
				MaxLOD = float.MaxValue,
				ShaderRegister = (uint)registers.Samplers[i],
				RegisterSpace = 0,
				ShaderVisibility = ShaderVisibility.All,
			};
		}

		var rootDesc = new RootSignatureDesc
		{
			NumParameters = (uint)paramCount,
			PParameters = parameters,
			NumStaticSamplers = (uint)template.InputCount,
			PStaticSamplers = template.InputCount > 0 ? samplers : null,
			Flags = RootSignatureFlags.None,
		};
		SerializeRootSignature(ctx, &rootDesc);

		// The compute bytecode is prebuilt (signed DXIL from the build, or FXC-compiled built-in bytes); pin it
		// for the CreatePipelineState call, which copies it.
		fixed (byte* pCs = template.FragmentBytecode)
		{
			var pso = new ComputePipelineStateDesc
			{
				PRootSignature = RootSignature.Handle,
				CS = new ShaderBytecode { PShaderBytecode = pCs, BytecodeLength = (nuint)template.FragmentBytecode.Length },
				NodeMask = 0,
				Flags = PipelineStateFlags.None,
			};
			CreateComputePipeline(ctx, &pso);
		}
	}

	// One descriptor range per binding, the k-th at the k-th binding's reflected register and the table's k-th
	// descriptor: the record path writes a table's descriptors in binding order, and registers needn't be contiguous.
	private static void FillRanges(DescriptorRange* ranges, DescriptorRangeType type, System.Collections.Immutable.ImmutableArray<int> registers, int count)
	{
		for (var i = 0; i < count; i++)
		{
			ranges[i] = new DescriptorRange
			{
				RangeType = type,
				NumDescriptors = 1,
				BaseShaderRegister = (uint)registers[i],
				RegisterSpace = 0,
				OffsetInDescriptorsFromTableStart = (uint)i,
			};
		}
	}

	private void BakeGraphics(DirectX12Context ctx, DirectX12TechniqueTemplate template, ReadOnlySpan<Format> colorFormats, Format depthFormat, bool depthReadOnly, int samples)
	{
		// Root signature: optionally a 32-bit root-constants block (the MVP for a mesh or particle technique, or a
		// 16-float scalar block for a post pass — one or the other, mirroring the Vulkan layout), and optionally one SRV
		// descriptor table + a static sampler per input. Every register is the one the DXIL reflection gives the
		// parameter (DirectX12Registers).
		// A draw-list technique (HasListPush) wants an all-stage root-constants block (vertex reads scale/translate,
		// pixel reads the tag/paper-white/hdr flag), so it pre-empts the vertex-only MVP push even though it also
		// reads a vertex stream.
		var hasRootConstants = template.HasFragmentPush || template.HasListPush;
		// Root constants + N uniform CBVs + one SRV table + M storage-buffer root SRVs.
		var parameters = stackalloc RootParameter[2 + template.UniformBufferCount + template.InputBufferCount];
		var paramCount = 0;
		var registers = template.Registers;
		// The SRV table: one range per sampled input, the k-th at the k-th texture's reflected register.
		var ranges = stackalloc DescriptorRange[Math.Max(1, template.InputCount)];
		FillRanges(ranges, DescriptorRangeType.Srv, registers.Textures, template.InputCount);

		if (hasRootConstants)
		{
			RootConstantParam = paramCount;
			parameters[paramCount++] = new RootParameter
			{
				ParameterType = RootParameterType.Type32BitConstants,
				ShaderVisibility = template.HasListPush ? ShaderVisibility.All : ShaderVisibility.Pixel,
				Anonymous = new RootParameterUnion { Constants = new RootConstants { ShaderRegister = (uint)registers.Push, RegisterSpace = 0, Num32BitValues = template.HasListPush ? ListPushDwords : 16u } },
			};
		}
		// The per-frame uniform buffers as root CBVs, one per reflected ConstantBuffer, each at the b register the DXIL
		// reflection gives it. Bound each frame from a Constants resource's UPLOAD address (a pass's .Reads) or the
		// uniform ring (an in-closure push). ShaderVisibility.All so they serve both a fullscreen pixel reader
		// (CSM/lighting params) and a vertex reader (foliage wind).
		if (template.UniformBufferCount > 0)
		{
			CbvRootParam = paramCount;
			for (var u = 0; u < template.UniformBufferCount; u++)
			{
				parameters[paramCount++] = new RootParameter
				{
					ParameterType = RootParameterType.TypeCbv,
					ShaderVisibility = ShaderVisibility.All,
					Anonymous = new RootParameterUnion { Descriptor = new RootDescriptor { ShaderRegister = (uint)registers.UniformBuffers[u], RegisterSpace = 0 } },
				};
			}
		}
		if (template.InputCount > 0)
		{
			TableParam = paramCount;
			parameters[paramCount++] = new RootParameter
			{
				ParameterType = RootParameterType.TypeDescriptorTable,
				ShaderVisibility = ShaderVisibility.Pixel,
				Anonymous = new RootParameterUnion { DescriptorTable = new RootDescriptorTable { NumDescriptorRanges = (uint)template.InputCount, PDescriptorRanges = ranges } },
			};
		}
		// App-writable storage buffers (g.Storage SSBOs) as read-only root SRVs, each at its reflected t register. Bound
		// from each Storage resource's UPLOAD address by a pass's .Reads, exactly as the compute skin path binds its
		// input buffers.
		if (template.InputBufferCount > 0)
		{
			InputBufferBaseParam = paramCount;
			for (var b = 0; b < template.InputBufferCount; b++)
			{
				parameters[paramCount++] = new RootParameter
				{
					ParameterType = RootParameterType.TypeSrv,
					ShaderVisibility = ShaderVisibility.All,
					Anonymous = new RootParameterUnion { Descriptor = new RootDescriptor { ShaderRegister = (uint)registers.InputBuffers[b], RegisterSpace = 0 } },
				};
			}
		}
		// One static sampler PER sampled input: Slang lowers each combined Sampler2D to a Texture2D + its OWN
		// SamplerState (a t register and an s register, both reflected), so a pass that samples N inputs references N.
		// A single shared sampler satisfies a 1-input pass (blit, the bloom down/up taps) but a 2+-input pass
		// (bloom composite, SSAO, deferred lighting) references s1.. — absent from the root signature, the PS would
		// reference a sampler the root signature doesn't declare and CreateGraphicsPipelineState fails. Static
		// samplers don't count against the 64-DWORD root budget, so N of them is free.
		// A slot the shader samples by comparison (SampleCmp) takes a filtered comparison sampler, clamped to the edge,
		// comparing by the technique's SampleCompare: the hardware's 2×2 PCF.
		var samplerCount = template.InputCount;
		var samplers = stackalloc StaticSamplerDesc[samplerCount > 0 ? samplerCount : 1];
		for (var i = 0; i < samplerCount; i++)
		{
			var compare = template.ComparedInputs is { } c && i < c.Length && c[i];
			samplers[i] = new StaticSamplerDesc
			{
				Filter = compare ? Filter.ComparisonMinMagLinearMipPoint : template.LinearFilter ? Filter.MinMagMipLinear : Filter.MinMagMipPoint,
				AddressU = compare ? TextureAddressMode.Clamp : ToTextureAddress(template.Address),
				AddressV = compare ? TextureAddressMode.Clamp : ToTextureAddress(template.Address),
				AddressW = compare ? TextureAddressMode.Clamp : ToTextureAddress(template.Address),
				ComparisonFunc = compare ? ToD3DCompare(template.SampleCompare!.Value) : ComparisonFunc.Never,
				BorderColor = StaticBorderColor.OpaqueBlack,
				MinLOD = 0f,
				MaxLOD = float.MaxValue,
				ShaderRegister = (uint)registers.Samplers[i],
				RegisterSpace = 0,
				ShaderVisibility = ShaderVisibility.Pixel,
			};
		}
		// A pass that reads any vertex stream (mesh, instanced, or particle) needs the input-assembler layout flag.
		var flags = template.Layout is not null || template.Instance is not null ? RootSignatureFlags.AllowInputAssemblerInputLayout : RootSignatureFlags.None;
		var rootDesc = new RootSignatureDesc
		{
			NumParameters = (uint)paramCount,
			PParameters = paramCount > 0 ? parameters : null,
			NumStaticSamplers = (uint)samplerCount,
			PStaticSamplers = samplerCount > 0 ? samplers : null,
			Flags = flags,
		};
		SerializeRootSignature(ctx, &rootDesc);

		// Blend equation: None writes opaquely, Additive sums src+dst (bloom/particles),
		// AlphaOver does src-alpha over-blend (transparency). All pair with a pass that .Blends the target.
		var (srcBlend, dstBlend) = template.Blend switch
		{
			BlendMode.Additive => (Blend.One, Blend.One),
			BlendMode.AlphaOver => (Blend.SrcAlpha, Blend.InvSrcAlpha),
			BlendMode.PremultipliedAlpha => (Blend.One, Blend.InvSrcAlpha),
			BlendMode.Multiply => (Blend.DestColor, Blend.Zero),
			BlendMode.None => (Blend.One, Blend.Zero),
			_ => throw new NotSupportedException($"DirectX12: BlendMode '{template.Blend}' is not mapped to blend factors."),
		};
		var blend = new BlendDesc { AlphaToCoverageEnable = template.AlphaToCoverage && samples > 1, IndependentBlendEnable = false };
		blend.RenderTarget[0] = new RenderTargetBlendDesc
		{
			BlendEnable = template.Blend != BlendMode.None,
			SrcBlend = srcBlend,
			DestBlend = dstBlend,
			BlendOp = BlendOp.Add,
			// D3D12 refuses a *_COLOR factor in the alpha equation (Multiply's DestColor): alpha takes its alpha twin
			SrcBlendAlpha = AlphaFactor(srcBlend),
			DestBlendAlpha = AlphaFactor(dstBlend),
			BlendOpAlpha = BlendOp.Add,
			LogicOpEnable = false,
			LogicOp = LogicOp.Noop,
			RenderTargetWriteMask = 0xF,
		};

		var hasDepth = depthFormat != Format.FormatUnknown;
		// The stage bytecode is prebuilt (signed DXIL from the build, or FXC-compiled built-in bytes); pin both
		// for the CreatePipelineState call, which copies them.
		fixed (byte* pVs = template.VertexBytecode)
		fixed (byte* pPs = template.FragmentBytecode)
		{
			var pso = new GraphicsPipelineStateDesc
			{
				PRootSignature = RootSignature.Handle,
				VS = new ShaderBytecode { PShaderBytecode = pVs, BytecodeLength = (nuint)template.VertexBytecode.Length },
				PS = new ShaderBytecode { PShaderBytecode = pPs, BytecodeLength = (nuint)template.FragmentBytecode.Length },
				BlendState = blend,
				SampleMask = uint.MaxValue,
				// Counter-clockwise winding is the front face (the glTF convention: triangles wound CCW when viewed
				// from outside face the camera), matching the Vulkan backend, so Cull.Back discards the same
				// away-facing triangles on either backend. D3D12 defaults to clockwise-front, hence the explicit flag.
				// D3D12 applies the constant term as Constant x 2^(exponent-23) for a float depth buffer, where Vulkan
				// scales it by an implementation-defined r. The same DepthBias.Constant therefore offsets by different
				// amounts on the two backends — documented on DepthBias; SlopeScale is the portable term.
				RasterizerState = new RasterizerDesc
				{
					FillMode = FillMode.Solid,
					CullMode = ToD3DCull(template.Cull),
					FrontCounterClockwise = true,
					DepthClipEnable = true,
					DepthBias = template.Bias?.Constant ?? 0,
					SlopeScaledDepthBias = template.Bias?.SlopeScale ?? 0f,
					DepthBiasClamp = template.Bias?.Clamp ?? 0f,
				},
				DepthStencilState = new DepthStencilDesc
				{
					DepthEnable = hasDepth,
					DepthWriteMask = depthReadOnly ? DepthWriteMask.Zero : DepthWriteMask.All,
					DepthFunc = ToD3DCompare(template.DepthTest),
					StencilEnable = false,
				},
				DSVFormat = hasDepth ? depthFormat : Format.FormatUnknown,
				PrimitiveTopologyType = PrimitiveTopologyType.Triangle,
				NumRenderTargets = (uint)colorFormats.Length,
				SampleDesc = new SampleDesc { Count = (uint)samples, Quality = 0 },   // the pass's targets' multisample count
				Flags = PipelineStateFlags.None,
			};
			for (var i = 0; i < colorFormats.Length && i < 8; i++)
				pso.RTVFormats[i] = colorFormats[i];

			// Vertex input: build the input layout from the technique's declared streams — an optional per-vertex
			// layout (the canonical interleaved BinaryVibrance.Kaldra.Core.Vertex POSITION/NORMAL/TEXCOORD/TANGENT, mirroring
			// lit.slang) and an optional per-instance layout (an instanced mesh's transform rows, or a GPU-particle
			// stream the compute filled). Each stream's input slot and classification come from the VertexLayout
			// itself, so a per-instance stream binds at slot 1 beside a mesh or owns slot 0 when there is no mesh
			// (particles). A fullscreen technique declares neither and fabricates its triangle, so it needs none.
			// Semantic names are packed null-terminated into one stack buffer; the HLSL semantic index is the count
			// of earlier attributes sharing the name (so four "ITRANSFORM" rows become ITRANSFORM0..3, two
			// "IPARTICLE" rows IPARTICLE0..1). DXGI formats + byte offsets come straight from the Core layout — the
			// same data the Vulkan attribute descriptions consume.
			if (template.Layout is not null || template.Instance is not null)
			{
				List<VertexLayout> streams = [];
				if (template.Layout is not null)
					streams.Add(template.Layout);
				if (template.Instance is not null)
					streams.Add(template.Instance);

				var total = 0;
				var nameCapacity = 0;
				foreach (var stream in streams)
				{
					total += stream.Attributes.Length;
					for (var i = 0; i < stream.Attributes.Length; i++)
						nameCapacity += stream.Attributes[i].Semantic.Length + 1;
				}

				var names = stackalloc byte[nameCapacity];
				var elements = stackalloc InputElementDesc[total];
				var nameOffset = 0;
				var e = 0;
				var semanticCounter = new Dictionary<string, uint>();

				foreach (var stream in streams)
				{
					var perInstance = stream.InputRate == VertexInputRate.PerInstance;
					for (var i = 0; i < stream.Attributes.Length; i++)
					{
						var attribute = stream.Attributes[i];
						var namePtr = names + nameOffset;
						for (var c = 0; c < attribute.Semantic.Length; c++)
							names[nameOffset++] = (byte)attribute.Semantic[c];
						names[nameOffset++] = 0;
						semanticCounter.TryGetValue(attribute.Semantic, out var index);
						semanticCounter[attribute.Semantic] = index + 1;
						elements[e++] = new InputElementDesc
						{
							SemanticName = namePtr,
							SemanticIndex = index,
							Format = ToDxgiFormat(attribute.Format),
							InputSlot = (uint)stream.Binding,
							AlignedByteOffset = (uint)stream.Offsets[i],
							InputSlotClass = perInstance ? InputClassification.PerInstanceData : InputClassification.PerVertexData,
							InstanceDataStepRate = perInstance ? 1u : 0u,
						};
					}
				}

				pso.InputLayout = new InputLayoutDesc { NumElements = (uint)total, PInputElementDescs = elements };
				CreateGraphicsPipeline(ctx, &pso);
			}
			else
			{
				pso.InputLayout = new InputLayoutDesc { NumElements = 0, PInputElementDescs = null };
				CreateGraphicsPipeline(ctx, &pso);
			}
		}
	}

	// Exhaustive so a new CullMode member fails loud here instead of silently disabling culling. The Core enum is
	// fully qualified because Silk's Direct3D12.CullMode (aliased D3DCullMode) shares the name in this file.
	private static D3DCullMode ToD3DCull(BinaryVibrance.Kaldra.Core.Graph.CullMode cull) => cull switch
	{
		BinaryVibrance.Kaldra.Core.Graph.CullMode.None => D3DCullMode.None,
		BinaryVibrance.Kaldra.Core.Graph.CullMode.Back => D3DCullMode.Back,
		BinaryVibrance.Kaldra.Core.Graph.CullMode.Front => D3DCullMode.Front,
		_ => throw new NotSupportedException($"DirectX12: CullMode '{cull}' is not mapped to a D3D12 cull mode."),
	};

	// Exhaustive so a new DepthCompare member fails loud here instead of silently keeping the previous test.
	private static ComparisonFunc ToD3DCompare(DepthCompare compare) => compare switch
	{
		DepthCompare.Never => ComparisonFunc.Never,
		DepthCompare.Less => ComparisonFunc.Less,
		DepthCompare.Equal => ComparisonFunc.Equal,
		DepthCompare.LessOrEqual => ComparisonFunc.LessEqual,
		DepthCompare.Greater => ComparisonFunc.Greater,
		DepthCompare.NotEqual => ComparisonFunc.NotEqual,
		DepthCompare.GreaterOrEqual => ComparisonFunc.GreaterEqual,
		DepthCompare.Always => ComparisonFunc.Always,
		_ => throw new NotSupportedException($"DirectX12: DepthCompare '{compare}' is not mapped to a comparison func."),
	};

	// A blend factor as the alpha equation may take it: D3D12 allows no *_COLOR factor there, so a colour factor becomes
	// the alpha factor of the same source (Vulkan and WebGPU take either, reading the alpha channel the same way)
	private static Blend AlphaFactor(Blend factor) => factor switch
	{
		Blend.SrcColor => Blend.SrcAlpha,
		Blend.InvSrcColor => Blend.InvSrcAlpha,
		Blend.DestColor => Blend.DestAlpha,
		Blend.InvDestColor => Blend.InvDestAlpha,
		_ => factor,
	};

	// Exhaustive so a new SamplerAddress member fails loud here instead of silently mapping to Clamp.
	private static TextureAddressMode ToTextureAddress(SamplerAddress address) => address switch
	{
		SamplerAddress.Clamp => TextureAddressMode.Clamp,
		SamplerAddress.Repeat => TextureAddressMode.Wrap,
		SamplerAddress.MirrorRepeat => TextureAddressMode.Mirror,
		SamplerAddress.ClampToBorder => TextureAddressMode.Border,
		SamplerAddress.MirrorClampToEdge => TextureAddressMode.MirrorOnce,
		_ => throw new NotSupportedException($"DirectX12: SamplerAddress '{address}' is not mapped to a TextureAddressMode."),
	};

	private static Format ToDxgiFormat(VertexFormat format) => format switch
	{
		VertexFormat.Float => Format.FormatR32Float,
		VertexFormat.Float2 => Format.FormatR32G32Float,
		VertexFormat.Float3 => Format.FormatR32G32B32Float,
		VertexFormat.Float4 => Format.FormatR32G32B32A32Float,
		VertexFormat.UInt4 => Format.FormatR32G32B32A32Uint,
		VertexFormat.Half4 => Format.FormatR16G16B16A16Float,
		VertexFormat.UNorm4 => Format.FormatR8G8B8A8Unorm,
		_ => throw new ArgumentOutOfRangeException(nameof(format), format, "Unknown vertex format."),
	};

	private void SerializeRootSignature(DirectX12Context ctx, RootSignatureDesc* rootDesc)
	{
		ComPtr<ID3D10Blob> blob = default;
		ComPtr<ID3D10Blob> error = default;
		SilkMarshal.ThrowHResult(ctx.D3D12.SerializeRootSignature(rootDesc, D3DRootSignatureVersion.Version1, ref blob, ref error));
		SilkMarshal.ThrowHResult(ctx.Device.CreateRootSignature(0, blob.GetBufferPointer(), blob.GetBufferSize(), out ComPtr<ID3D12RootSignature> madeRootSignature));
		RootSignature = madeRootSignature;
		blob.Dispose();
		if (error.Handle is not null)
			error.Dispose();
	}

	public void Dispose()
	{
		Pipeline.Dispose();
		RootSignature.Dispose();
	}
}
