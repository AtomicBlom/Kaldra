using Silk.NET.Core.Native;
using Silk.NET.Direct3D.Compilers;
using Silk.NET.Direct3D12;
using Silk.NET.DXGI;

namespace Loom.DX12;

/// <summary>A registered DX12 technique: its HLSL + the shape needed to bake a pipeline (the DX12 counterpart of <c>TechniqueTemplate</c>).</summary>
internal readonly record struct DX12TechniqueTemplate(string Hlsl, string VertexEntry, string FragmentEntry, int InputCount, bool HasFragmentPush, bool LinearFilter);

/// <summary>
/// Bakes and caches DX12 graphics pipelines per technique — the counterpart of the Vulkan backend's
/// <c>TechniqueCache</c>. A pipeline is keyed by the technique name plus the pass's actual render-target
/// formats (a PSO is format-specific, exactly like a Vulkan dynamic-rendering pipeline). The HLSL is compiled
/// once per unique (name, formats) combination and the PSO cached for the engine's lifetime.
/// <para>
/// This first executor step bakes only from-scratch passes (no sampled inputs, no push constants), so the
/// root signature is empty; the descriptor-table + static-sampler + root-constant path lands with the
/// transient/sample slice.
/// </para>
/// </summary>
internal sealed unsafe class DX12TechniqueCache : IDisposable
{
	private readonly DX12Context _ctx;
	private readonly Dictionary<string, DX12TechniqueTemplate> _templates = [];
	private readonly Dictionary<string, DX12Technique> _baked = [];

	public DX12TechniqueCache(DX12Context ctx) => _ctx = ctx;

	public void Register(string name, DX12TechniqueTemplate template) => _templates[name] = template;

	public DX12Technique GetOrBake(string name, ReadOnlySpan<Format> colorFormats)
	{
		if (!_templates.TryGetValue(name, out var template))
			throw new InvalidOperationException($"DX12: technique '{name}' was not defined — call DefineTechnique first.");

		var key = BuildKey(name, colorFormats);
		if (_baked.TryGetValue(key, out var cached))
			return cached;

		var technique = new DX12Technique(_ctx, template, colorFormats);
		_baked[key] = technique;
		return technique;
	}

	private static string BuildKey(string name, ReadOnlySpan<Format> colorFormats)
	{
		var key = name;
		foreach (var format in colorFormats)
			key += "|" + (int)format;
		return key;
	}

	public void Dispose()
	{
		foreach (var technique in _baked.Values)
			technique.Dispose();
		_baked.Clear();
	}
}

/// <summary>One baked graphics pipeline + its root signature.</summary>
internal sealed unsafe class DX12Technique : IDisposable
{
	public ComPtr<ID3D12RootSignature> RootSignature;
	public ComPtr<ID3D12PipelineState> Pipeline;

	public DX12Technique(DX12Context ctx, DX12TechniqueTemplate template, ReadOnlySpan<Format> colorFormats)
	{
		if (template.HasFragmentPush)
			throw new NotSupportedException("DX12: fragment push constants land in a later executor slice.");

		using var compiler = D3DCompiler.GetApi();
		var hlsl = System.Text.Encoding.ASCII.GetBytes(template.Hlsl);
		var vs = DX12Shaders.Compile(compiler, hlsl, template.VertexEntry, "vs_5_0");
		var ps = DX12Shaders.Compile(compiler, hlsl, template.FragmentEntry, "ps_5_0");

		// A from-scratch pass (no inputs) needs an empty root signature; a sampling pass binds N sampled inputs
		// through one SRV descriptor table (register t0..tN-1) plus one static sampler (s0).
		RootSignatureDesc rootDesc;
		var range = new DescriptorRange
		{
			RangeType = DescriptorRangeType.Srv,
			NumDescriptors = (uint)template.InputCount,
			BaseShaderRegister = 0,
			RegisterSpace = 0,
			OffsetInDescriptorsFromTableStart = 0
		};
		var param = new RootParameter
		{
			ParameterType = RootParameterType.TypeDescriptorTable,
			ShaderVisibility = ShaderVisibility.Pixel,
			Anonymous = new RootParameterUnion { DescriptorTable = new RootDescriptorTable { NumDescriptorRanges = 1, PDescriptorRanges = &range } }
		};
		var sampler = new StaticSamplerDesc
		{
			Filter = template.LinearFilter ? Filter.MinMagMipLinear : Filter.MinMagMipPoint,
			AddressU = TextureAddressMode.Clamp,
			AddressV = TextureAddressMode.Clamp,
			AddressW = TextureAddressMode.Clamp,
			ComparisonFunc = ComparisonFunc.Never,
			BorderColor = StaticBorderColor.OpaqueBlack,
			MinLOD = 0f,
			MaxLOD = float.MaxValue,
			ShaderRegister = 0,
			RegisterSpace = 0,
			ShaderVisibility = ShaderVisibility.Pixel
		};
		if (template.InputCount > 0)
			rootDesc = new RootSignatureDesc { NumParameters = 1, PParameters = &param, NumStaticSamplers = 1, PStaticSamplers = &sampler, Flags = RootSignatureFlags.None };
		else
			rootDesc = new RootSignatureDesc { Flags = RootSignatureFlags.None };

		ComPtr<ID3D10Blob> blob = default;
		ComPtr<ID3D10Blob> error = default;
		SilkMarshal.ThrowHResult(ctx.D3D12.SerializeRootSignature(&rootDesc, D3DRootSignatureVersion.Version1, ref blob, ref error));
		SilkMarshal.ThrowHResult(ctx.Device.CreateRootSignature(0, blob.GetBufferPointer(), blob.GetBufferSize(), out RootSignature));
		blob.Dispose();
		if (error.Handle is not null)
			error.Dispose();

		var blend = new BlendDesc { AlphaToCoverageEnable = false, IndependentBlendEnable = false };
		blend.RenderTarget[0] = new RenderTargetBlendDesc { RenderTargetWriteMask = 0xF };

		var pso = new GraphicsPipelineStateDesc
		{
			PRootSignature = RootSignature.Handle,
			VS = new ShaderBytecode { PShaderBytecode = vs.GetBufferPointer(), BytecodeLength = vs.GetBufferSize() },
			PS = new ShaderBytecode { PShaderBytecode = ps.GetBufferPointer(), BytecodeLength = ps.GetBufferSize() },
			BlendState = blend,
			SampleMask = uint.MaxValue,
			RasterizerState = new RasterizerDesc { FillMode = FillMode.Solid, CullMode = CullMode.None, FrontCounterClockwise = false, DepthClipEnable = true },
			DepthStencilState = new DepthStencilDesc { DepthEnable = false, StencilEnable = false },
			InputLayout = new InputLayoutDesc { NumElements = 0, PInputElementDescs = null },
			PrimitiveTopologyType = PrimitiveTopologyType.Triangle,
			NumRenderTargets = (uint)colorFormats.Length,
			SampleDesc = new SampleDesc { Count = 1, Quality = 0 },
			Flags = PipelineStateFlags.None
		};
		for (var i = 0; i < colorFormats.Length && i < 8; i++)
			pso.RTVFormats[i] = colorFormats[i];
		SilkMarshal.ThrowHResult(ctx.Device.CreateGraphicsPipelineState(&pso, out Pipeline));

		vs.Dispose();
		ps.Dispose();
	}

	public void Dispose()
	{
		Pipeline.Dispose();
		RootSignature.Dispose();
	}
}
