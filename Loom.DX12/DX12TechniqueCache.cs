using Silk.NET.Core.Native;
using Silk.NET.Direct3D.Compilers;
using Silk.NET.Direct3D12;
using Silk.NET.DXGI;

using BlendMode = Loom.Core.BlendMode;
using SamplerAddress = Loom.Core.SamplerAddress;

namespace Loom.DX12;

/// <summary>
/// A registered DX12 technique: its <em>prebuilt per-stage bytecode</em> + the shape needed to bake a pipeline
/// (the DX12 counterpart of <c>TechniqueTemplate</c>). The bytecode is either signed DXIL the Loom build emitted
/// offline (the real Slang shaders) or DXBC the backend FXC-compiled from a built-in stand-in — both are blobs
/// the PSO consumes directly, so baking never compiles. The vertex and fragment stages are separate blobs; for
/// a compute technique only <see cref="FragmentBytecode"/> (the compute blob) is used.
/// </summary>
internal readonly record struct DX12TechniqueTemplate(
	byte[] VertexBytecode, byte[] FragmentBytecode, string VertexEntry, string FragmentEntry, int InputCount,
	bool HasVertexInput, bool HasFragmentPush, bool LinearFilter,
	bool IsCompute = false, bool ParticleInput = false, BlendMode Blend = BlendMode.None,
	int StorageImages = 0, int StorageBuffers = 0, int StorageBufferStride = 0,
	SamplerAddress Address = SamplerAddress.Clamp,
	Loom.Core.VertexLayout? Layout = null, Loom.Core.VertexLayout? Instance = null);

/// <summary>
/// Bakes and caches DX12 pipelines per technique — the counterpart of the Vulkan backend's
/// <c>TechniqueCache</c>. A graphics pipeline is keyed by the technique name plus the pass's actual
/// render-target formats <em>and depth format</em> (a PSO is format-specific, exactly like a Vulkan
/// dynamic-rendering pipeline). The HLSL is compiled once per unique key and the PSO cached for the
/// engine's lifetime.
/// <para>
/// Bakes from-scratch fullscreen passes, sampling passes (SRV table + static sampler), mesh passes (vertex
/// input layout + depth test + an MVP root constant), additive/alpha blend, compute passes (a root-UAV
/// storage buffer + a scalar root constant), and the per-instance particle billboard path.
/// </para>
/// </summary>
internal sealed unsafe class DX12TechniqueCache : IDisposable
{
	private readonly DX12Context _ctx;
	private readonly Dictionary<string, DX12TechniqueTemplate> _templates = [];
	private readonly Dictionary<string, DX12Technique> _baked = [];

	public DX12TechniqueCache(DX12Context ctx) => _ctx = ctx;

	public void Register(string name, DX12TechniqueTemplate template) => _templates[name] = template;

	public DX12Technique GetOrBake(string name, ReadOnlySpan<Format> colorFormats, Format depthFormat, bool depthReadOnly)
	{
		if (!_templates.TryGetValue(name, out var template))
			throw new InvalidOperationException($"DX12: technique '{name}' was not defined — call DefineTechnique first.");

		var key = BuildKey(name, colorFormats, depthFormat, depthReadOnly);
		if (_baked.TryGetValue(key, out var cached))
			return cached;

		var technique = new DX12Technique(_ctx, template, colorFormats, depthFormat, depthReadOnly);
		// Name the baked PSO + root signature for GPU debuggers (DX12 has no separate shader objects — the DXIL
		// lives in the PSO, so naming the PSO is the shader-naming equivalent). [Conditional]: stripped without
		// UseNamedGPUObjects. Distinct target-format variants of one technique share the logical name.
		_ctx.SetObjectName((ID3D12Object*)technique.Pipeline.Handle, $"pipeline:{name}");
		_ctx.SetObjectName((ID3D12Object*)technique.RootSignature.Handle, $"rootsig:{name}");
		_baked[key] = technique;
		return technique;
	}

	private static string BuildKey(string name, ReadOnlySpan<Format> colorFormats, Format depthFormat, bool depthReadOnly)
	{
		var key = name;
		foreach (var format in colorFormats)
			key += "|" + (int)format;
		key += "|d" + (int)depthFormat + (depthReadOnly ? "r" : "w");
		return key;
	}

	/// <summary>Dispose every baked PSO and empty the bake cache — used by shader hot-reload (M13) to force a fresh
	/// bake from re-registered templates. Templates are kept (re-registered by DefineTechnique). Drain the GPU first.</summary>
	public void Reset()
	{
		foreach (var technique in _baked.Values)
			technique.Dispose();
		_baked.Clear();
	}

	public void Dispose() => Reset();
}

/// <summary>One baked pipeline + its root signature, with the root-parameter slots the context binds against.</summary>
internal sealed unsafe class DX12Technique : IDisposable
{
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

	/// <summary>Root-parameter slot for the storage-image / structured-buffer UAV descriptor table (a compute's <c>.ReadWrites</c> of a texture or structured buffer), or -1 if none.</summary>
	public int UavTableParam { get; private set; } = -1;

	/// <summary>Element stride (bytes) of a structured-buffer storage resource, for building its UAV descriptor's <c>StructureByteStride</c>/<c>NumElements</c>; 0 for a texture or raw buffer.</summary>
	public int StorageBufferStride { get; private set; }

	public DX12Technique(DX12Context ctx, DX12TechniqueTemplate template, ReadOnlySpan<Format> colorFormats, Format depthFormat, bool depthReadOnly)
	{
		IsCompute = template.IsCompute;
		StorageBufferStride = template.StorageBufferStride;
		InputCount = template.InputCount;

		if (template.IsCompute)
		{
			BakeCompute(ctx, template);
			return;
		}
		BakeGraphics(ctx, template, colorFormats, depthFormat, depthReadOnly);
	}

	// A compute root signature shaped to the technique: a 16-float scalar root constant at b0 (always) + an
	// optional t0.. SRV table for sampled inputs (e.g. the auto-exposure compute metering the lit scene) + the
	// storage binding — a RW texture (the 1×1 exposure image) OR a RW STRUCTURED buffer (the GPU-particle stream)
	// via a u0 UAV descriptor TABLE, since both need a real descriptor (a structured buffer's StructureByteStride/
	// NumElements is what makes the shader's GetDimensions return a non-zero count; a root UAV is a raw GPU address
	// with no metadata, so GetDimensions yields 0 and the particle update early-outs every thread). Only a RAW
	// buffer with no reflected stride (the FXC built-in's ByteAddressBuffer) keeps the u0 root-UAV-by-address path.
	// No compute technique mixes an image and a buffer, so u0 is unambiguous. All visible to ShaderVisibility.All
	// (compute isn't covered by the per-stage VERTEX/PIXEL visibilities). Static samplers s0.. pair with the SRV table.
	private void BakeCompute(DX12Context ctx, DX12TechniqueTemplate template)
	{
		var parameters = stackalloc RootParameter[3];
		var paramCount = 0;
		var uavTableCount = template.StorageImages > 0 ? template.StorageImages : template.StorageBuffers;
		var srvRange = new DescriptorRange { RangeType = DescriptorRangeType.Srv, NumDescriptors = (uint)template.InputCount, BaseShaderRegister = 0, RegisterSpace = 0, OffsetInDescriptorsFromTableStart = 0 };
		var uavRange = new DescriptorRange { RangeType = DescriptorRangeType.Uav, NumDescriptors = (uint)uavTableCount, BaseShaderRegister = 0, RegisterSpace = 0, OffsetInDescriptorsFromTableStart = 0 };
		// A structured buffer (stride known from reflection) and a storage image both bind via the u0 UAV table;
		// only a raw buffer (stride 0) falls back to a root UAV.
		var bufferViaTable = template.StorageBuffers > 0 && template.StorageBufferStride > 0;

		RootConstantParam = paramCount;
		parameters[paramCount++] = new RootParameter
		{
			ParameterType = RootParameterType.Type32BitConstants,
			ShaderVisibility = ShaderVisibility.All,
			Anonymous = new RootParameterUnion { Constants = new RootConstants { ShaderRegister = 0, RegisterSpace = 0, Num32BitValues = 16 } },
		};
		if (template.InputCount > 0)
		{
			TableParam = paramCount;
			parameters[paramCount++] = new RootParameter
			{
				ParameterType = RootParameterType.TypeDescriptorTable,
				ShaderVisibility = ShaderVisibility.All,
				Anonymous = new RootParameterUnion { DescriptorTable = new RootDescriptorTable { NumDescriptorRanges = 1, PDescriptorRanges = &srvRange } },
			};
		}
		if (template.StorageImages > 0 || bufferViaTable)
		{
			UavTableParam = paramCount;
			parameters[paramCount++] = new RootParameter
			{
				ParameterType = RootParameterType.TypeDescriptorTable,
				ShaderVisibility = ShaderVisibility.All,
				Anonymous = new RootParameterUnion { DescriptorTable = new RootDescriptorTable { NumDescriptorRanges = 1, PDescriptorRanges = &uavRange } },
			};
		}
		else if (template.StorageBuffers > 0)
		{
			UavRootParam = paramCount;
			parameters[paramCount++] = new RootParameter
			{
				ParameterType = RootParameterType.TypeUav,
				ShaderVisibility = ShaderVisibility.All,
				Anonymous = new RootParameterUnion { Descriptor = new RootDescriptor { ShaderRegister = 0, RegisterSpace = 0 } },
			};
		}

		var samplers = stackalloc StaticSamplerDesc[template.InputCount > 0 ? template.InputCount : 1];
		for (var i = 0; i < template.InputCount; i++)
			samplers[i] = new StaticSamplerDesc
			{
				Filter = template.LinearFilter ? Filter.MinMagMipLinear : Filter.MinMagMipPoint,
				AddressU = template.Address == SamplerAddress.Repeat ? TextureAddressMode.Wrap : TextureAddressMode.Clamp,
				AddressV = template.Address == SamplerAddress.Repeat ? TextureAddressMode.Wrap : TextureAddressMode.Clamp,
				AddressW = template.Address == SamplerAddress.Repeat ? TextureAddressMode.Wrap : TextureAddressMode.Clamp,
				ComparisonFunc = ComparisonFunc.Never,
				BorderColor = StaticBorderColor.OpaqueBlack,
				MinLOD = 0f,
				MaxLOD = float.MaxValue,
				ShaderRegister = (uint)i,
				RegisterSpace = 0,
				ShaderVisibility = ShaderVisibility.All,
			};

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
			SilkMarshal.ThrowHResult(ctx.Device.CreateComputePipelineState(&pso, out Pipeline));
		}
	}

	private void BakeGraphics(DX12Context ctx, DX12TechniqueTemplate template, ReadOnlySpan<Format> colorFormats, Format depthFormat, bool depthReadOnly)
	{
		// Root signature: optionally a 32-bit root-constants block (the MVP at b0/vertex for a mesh or particle
		// technique, or a 16-float scalar block at b0/pixel for a post pass — one or the other, mirroring the
		// Vulkan layout), and optionally one SRV descriptor table (t0..tN-1) + a static sampler (s0).
		var hasMvpPush = template.HasVertexInput || template.ParticleInput;
		var hasRootConstants = hasMvpPush || template.HasFragmentPush;
		var parameters = stackalloc RootParameter[2];
		var paramCount = 0;
		var range = new DescriptorRange
		{
			RangeType = DescriptorRangeType.Srv,
			NumDescriptors = (uint)template.InputCount,
			BaseShaderRegister = 0,
			RegisterSpace = 0,
			OffsetInDescriptorsFromTableStart = 0,
		};

		if (hasRootConstants)
		{
			RootConstantParam = paramCount;
			parameters[paramCount++] = new RootParameter
			{
				ParameterType = RootParameterType.Type32BitConstants,
				ShaderVisibility = hasMvpPush ? ShaderVisibility.Vertex : ShaderVisibility.Pixel,
				Anonymous = new RootParameterUnion { Constants = new RootConstants { ShaderRegister = 0, RegisterSpace = 0, Num32BitValues = 16 } },
			};
		}
		if (template.InputCount > 0)
		{
			TableParam = paramCount;
			parameters[paramCount++] = new RootParameter
			{
				ParameterType = RootParameterType.TypeDescriptorTable,
				ShaderVisibility = ShaderVisibility.Pixel,
				Anonymous = new RootParameterUnion { DescriptorTable = new RootDescriptorTable { NumDescriptorRanges = 1, PDescriptorRanges = &range } },
			};
		}
		// One static sampler PER sampled input: Slang lowers each combined Sampler2D to a Texture2D + its OWN
		// SamplerState (texture t_i paired with sampler s_i), so a pass that samples N inputs references s0..s(N-1).
		// A single shared sampler (s0) satisfies a 1-input pass (blit, the bloom down/up taps) but a 2+-input pass
		// (bloom composite, SSAO, deferred lighting) references s1.. — absent from the root signature, the PS would
		// reference a sampler the root signature doesn't declare and CreateGraphicsPipelineState fails. Static
		// samplers don't count against the 64-DWORD root budget, so N of them is free.
		var samplerCount = template.InputCount;
		var samplers = stackalloc StaticSamplerDesc[samplerCount > 0 ? samplerCount : 1];
		for (var i = 0; i < samplerCount; i++)
			samplers[i] = new StaticSamplerDesc
			{
				Filter = template.LinearFilter ? Filter.MinMagMipLinear : Filter.MinMagMipPoint,
				AddressU = template.Address == SamplerAddress.Repeat ? TextureAddressMode.Wrap : TextureAddressMode.Clamp,
				AddressV = template.Address == SamplerAddress.Repeat ? TextureAddressMode.Wrap : TextureAddressMode.Clamp,
				AddressW = template.Address == SamplerAddress.Repeat ? TextureAddressMode.Wrap : TextureAddressMode.Clamp,
				ComparisonFunc = ComparisonFunc.Never,
				BorderColor = StaticBorderColor.OpaqueBlack,
				MinLOD = 0f,
				MaxLOD = float.MaxValue,
				ShaderRegister = (uint)i,
				RegisterSpace = 0,
				ShaderVisibility = ShaderVisibility.Pixel,
			};
		// A mesh/particle pass reads a vertex buffer, so the root signature must allow the input-assembler layout.
		var flags = template.HasVertexInput || template.ParticleInput ? RootSignatureFlags.AllowInputAssemblerInputLayout : RootSignatureFlags.None;
		var rootDesc = new RootSignatureDesc
		{
			NumParameters = (uint)paramCount,
			PParameters = paramCount > 0 ? parameters : null,
			NumStaticSamplers = (uint)samplerCount,
			PStaticSamplers = samplerCount > 0 ? samplers : null,
			Flags = flags,
		};
		SerializeRootSignature(ctx, &rootDesc);

		// Blend equation (Planning.md §7): None writes opaquely, Additive sums src+dst (bloom/particles),
		// AlphaOver does src-alpha over-blend (transparency). All pair with a pass that .Blends the target.
		var (srcBlend, dstBlend) = template.Blend switch
		{
			BlendMode.Additive => (Blend.One, Blend.One),
			BlendMode.AlphaOver => (Blend.SrcAlpha, Blend.InvSrcAlpha),
			_ => (Blend.One, Blend.Zero),
		};
		var blend = new BlendDesc { AlphaToCoverageEnable = false, IndependentBlendEnable = false };
		blend.RenderTarget[0] = new RenderTargetBlendDesc
		{
			BlendEnable = template.Blend != BlendMode.None,
			SrcBlend = srcBlend,
			DestBlend = dstBlend,
			BlendOp = BlendOp.Add,
			SrcBlendAlpha = srcBlend,
			DestBlendAlpha = dstBlend,
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
				RasterizerState = new RasterizerDesc { FillMode = FillMode.Solid, CullMode = CullMode.None, FrontCounterClockwise = false, DepthClipEnable = true },
				DepthStencilState = new DepthStencilDesc
				{
					DepthEnable = hasDepth,
					DepthWriteMask = depthReadOnly ? DepthWriteMask.Zero : DepthWriteMask.All,
					DepthFunc = ComparisonFunc.LessEqual,
					StencilEnable = false,
				},
				DSVFormat = hasDepth ? depthFormat : Format.FormatUnknown,
				PrimitiveTopologyType = PrimitiveTopologyType.Triangle,
				NumRenderTargets = (uint)colorFormats.Length,
				SampleDesc = new SampleDesc { Count = 1, Quality = 0 },
				Flags = PipelineStateFlags.None,
			};
			for (var i = 0; i < colorFormats.Length && i < 8; i++)
				pso.RTVFormats[i] = colorFormats[i];

			// Vertex input: a mesh reads the canonical interleaved Loom.Core.Vertex (POSITION/NORMAL/TEXCOORD,
			// mirroring lit.slang); a particle technique reads one float4 PER INSTANCE (the storage buffer the
			// compute wrote, expanded into a billboard); a fullscreen technique fabricates its triangle and needs none.
			if (template.Layout is not null)
			{
				// Build the input layout from the technique's declared per-vertex (slot 0) and optional
				// per-instance (slot 1, PerInstanceData) layouts (M12). Semantic names are packed null-terminated
				// into one stack buffer; the HLSL semantic index is the count of earlier attributes sharing the
				// name (so four "ITRANSFORM" rows become ITRANSFORM0..3). DXGI formats + byte offsets come straight
				// from the Core layout — the same data the Vulkan attribute descriptions consume.
				var vertexLayout = template.Layout;
				var instanceLayout = template.Instance;
				var total = vertexLayout.Attributes.Length + (instanceLayout?.Attributes.Length ?? 0);

				var nameCapacity = 0;
				for (var i = 0; i < vertexLayout.Attributes.Length; i++)
					nameCapacity += vertexLayout.Attributes[i].Semantic.Length + 1;
				if (instanceLayout is not null)
					for (var i = 0; i < instanceLayout.Attributes.Length; i++)
						nameCapacity += instanceLayout.Attributes[i].Semantic.Length + 1;

				var names = stackalloc byte[nameCapacity];
				var elements = stackalloc InputElementDesc[total];
				var nameOffset = 0;
				var e = 0;
				var semanticCounter = new Dictionary<string, uint>();

				for (var i = 0; i < vertexLayout.Attributes.Length; i++)
				{
					var attribute = vertexLayout.Attributes[i];
					var namePtr = names + nameOffset;
					for (var c = 0; c < attribute.Semantic.Length; c++)
						names[nameOffset++] = (byte)attribute.Semantic[c];
					names[nameOffset++] = 0;
					semanticCounter.TryGetValue(attribute.Semantic, out var index);
					semanticCounter[attribute.Semantic] = index + 1;
					elements[e++] = new InputElementDesc { SemanticName = namePtr, SemanticIndex = index, Format = ToDxgiFormat(attribute.Format), InputSlot = 0, AlignedByteOffset = (uint)vertexLayout.Offsets[i], InputSlotClass = InputClassification.PerVertexData, InstanceDataStepRate = 0 };
				}
				if (instanceLayout is not null)
					for (var i = 0; i < instanceLayout.Attributes.Length; i++)
					{
						var attribute = instanceLayout.Attributes[i];
						var namePtr = names + nameOffset;
						for (var c = 0; c < attribute.Semantic.Length; c++)
							names[nameOffset++] = (byte)attribute.Semantic[c];
						names[nameOffset++] = 0;
						semanticCounter.TryGetValue(attribute.Semantic, out var index);
						semanticCounter[attribute.Semantic] = index + 1;
						elements[e++] = new InputElementDesc { SemanticName = namePtr, SemanticIndex = index, Format = ToDxgiFormat(attribute.Format), InputSlot = 1, AlignedByteOffset = (uint)instanceLayout.Offsets[i], InputSlotClass = InputClassification.PerInstanceData, InstanceDataStepRate = 1 };
					}

				pso.InputLayout = new InputLayoutDesc { NumElements = (uint)total, PInputElementDescs = elements };
				SilkMarshal.ThrowHResult(ctx.Device.CreateGraphicsPipelineState(&pso, out Pipeline));
			}
			else if (template.ParticleInput)
			{
				ReadOnlySpan<byte> position = "POSITION\0"u8;
				fixed (byte* pPos = position)
				{
					var element = new InputElementDesc { SemanticName = pPos, SemanticIndex = 0, Format = Format.FormatR32G32B32A32Float, InputSlot = 0, AlignedByteOffset = 0, InputSlotClass = InputClassification.PerInstanceData, InstanceDataStepRate = 1 };
					pso.InputLayout = new InputLayoutDesc { NumElements = 1, PInputElementDescs = &element };
					SilkMarshal.ThrowHResult(ctx.Device.CreateGraphicsPipelineState(&pso, out Pipeline));
				}
			}
			else
			{
				pso.InputLayout = new InputLayoutDesc { NumElements = 0, PInputElementDescs = null };
				SilkMarshal.ThrowHResult(ctx.Device.CreateGraphicsPipelineState(&pso, out Pipeline));
			}
		}
	}

	private static Format ToDxgiFormat(Loom.Core.VertexFormat format) => format switch
	{
		Loom.Core.VertexFormat.Float => Format.FormatR32Float,
		Loom.Core.VertexFormat.Float2 => Format.FormatR32G32Float,
		Loom.Core.VertexFormat.Float3 => Format.FormatR32G32B32Float,
		Loom.Core.VertexFormat.Float4 => Format.FormatR32G32B32A32Float,
		_ => throw new ArgumentOutOfRangeException(nameof(format), format, "Unknown vertex format."),
	};

	private void SerializeRootSignature(DX12Context ctx, RootSignatureDesc* rootDesc)
	{
		ComPtr<ID3D10Blob> blob = default;
		ComPtr<ID3D10Blob> error = default;
		SilkMarshal.ThrowHResult(ctx.D3D12.SerializeRootSignature(rootDesc, D3DRootSignatureVersion.Version1, ref blob, ref error));
		SilkMarshal.ThrowHResult(ctx.Device.CreateRootSignature(0, blob.GetBufferPointer(), blob.GetBufferSize(), out RootSignature));
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
