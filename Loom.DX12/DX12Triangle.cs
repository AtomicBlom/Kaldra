using System.Runtime.CompilerServices;
using System.Text;

using Silk.NET.Core.Native;
using Silk.NET.Direct3D.Compilers;
using Silk.NET.Direct3D12;
using Silk.NET.DXGI;
using Silk.NET.Maths;

namespace Loom.DX12;

/// <summary>
/// The hand-baked triangle — the DX12 backend's M1 equivalent. A from-scratch pipeline (empty root signature,
/// no vertex buffer: the three positions/colours are fabricated in the vertex shader from <c>SV_VertexID</c>,
/// mirroring the Vulkan backend's fullscreen-triangle trick). It proves the bake path end to end: HLSL → DXBC,
/// root signature, PSO, viewport/scissor, draw.
/// <para>
/// The shader is compiled at runtime with the in-box FXC (<c>D3DCompile</c>) to DXBC shader model 5.0, which
/// D3D12 accepts directly. The bundled <c>slangc</c> can't emit DXIL on win-arm64 (no dxc/dxil shipped), so the
/// offline DXIL pipeline — the real path once techniques arrive — is a later workstream; this keeps slice 2
/// dependency-free.
/// </para>
/// </summary>
internal sealed unsafe class DX12Triangle : IDisposable
{
	// Clip space here is D3D's: +Y up, z in [0,1]. No Y-flip (FrameInputs documents "identity on D3D12").
	private const string Hlsl = """
		struct VSOut { float4 pos : SV_Position; float3 color : COLOR0; };

		VSOut VSMain(uint vid : SV_VertexID)
		{
			float2 positions[3] = { float2(0.0, 0.6), float2(0.6, -0.6), float2(-0.6, -0.6) };
			float3 colors[3]    = { float3(1.0, 0.3, 0.2), float3(0.3, 1.0, 0.3), float3(0.3, 0.45, 1.0) };
			VSOut o;
			o.pos   = float4(positions[vid], 0.0, 1.0);
			o.color = colors[vid];
			return o;
		}

		float4 PSMain(VSOut i) : SV_Target { return float4(i.color, 1.0); }
		""";

	private ComPtr<ID3D12RootSignature> _rootSignature;
	private ComPtr<ID3D12PipelineState> _pipelineState;

	public DX12Triangle(DX12Context ctx, Silk.NET.DXGI.Format renderTargetFormat)
	{
		using var compiler = D3DCompiler.GetApi();
		var hlsl = Encoding.ASCII.GetBytes(Hlsl);
		var vs = Compile(compiler, hlsl, "VSMain", "vs_5_0");
		var ps = Compile(compiler, hlsl, "PSMain", "ps_5_0");

		// Empty root signature: this draw binds no resources or root constants.
		var rootDesc = new RootSignatureDesc
		{
			NumParameters = 0,
			PParameters = null,
			NumStaticSamplers = 0,
			PStaticSamplers = null,
			Flags = RootSignatureFlags.None
		};
		ComPtr<ID3D10Blob> rootBlob = default;
		ComPtr<ID3D10Blob> rootError = default;
		SilkMarshal.ThrowHResult(ctx.D3D12.SerializeRootSignature(&rootDesc, D3DRootSignatureVersion.Version1, ref rootBlob, ref rootError));
		SilkMarshal.ThrowHResult(ctx.Device.CreateRootSignature(0, rootBlob.GetBufferPointer(), rootBlob.GetBufferSize(), out _rootSignature));
		rootBlob.Dispose();
		if (rootError.Handle is not null)
			rootError.Dispose();

		var blend = new BlendDesc { AlphaToCoverageEnable = false, IndependentBlendEnable = false };
		blend.RenderTarget[0] = new RenderTargetBlendDesc { RenderTargetWriteMask = 0xF /* all channels */ };

		var pso = new GraphicsPipelineStateDesc
		{
			PRootSignature = _rootSignature.Handle,
			VS = new ShaderBytecode { PShaderBytecode = vs.GetBufferPointer(), BytecodeLength = vs.GetBufferSize() },
			PS = new ShaderBytecode { PShaderBytecode = ps.GetBufferPointer(), BytecodeLength = ps.GetBufferSize() },
			BlendState = blend,
			SampleMask = uint.MaxValue,
			RasterizerState = new RasterizerDesc
			{
				FillMode = FillMode.Solid,
				CullMode = CullMode.None,
				FrontCounterClockwise = false,
				DepthClipEnable = true
			},
			DepthStencilState = new DepthStencilDesc { DepthEnable = false, StencilEnable = false },
			InputLayout = new InputLayoutDesc { NumElements = 0, PInputElementDescs = null },
			PrimitiveTopologyType = PrimitiveTopologyType.Triangle,
			NumRenderTargets = 1,
			SampleDesc = new SampleDesc { Count = 1, Quality = 0 },
			Flags = PipelineStateFlags.None
		};
		pso.RTVFormats[0] = renderTargetFormat;
		SilkMarshal.ThrowHResult(ctx.Device.CreateGraphicsPipelineState(&pso, out _pipelineState));

		vs.Dispose();
		ps.Dispose();
	}

	/// <summary>Record the triangle into <paramref name="cmd"/> (the backbuffer is already bound as the target).</summary>
	public void Record(ComPtr<ID3D12GraphicsCommandList> cmd, uint width, uint height)
	{
		var viewport = new Viewport { TopLeftX = 0, TopLeftY = 0, Width = width, Height = height, MinDepth = 0, MaxDepth = 1 };
		var scissor = new Box2D<int>(0, 0, (int)width, (int)height);
		cmd.RSSetViewports(1, &viewport);
		cmd.RSSetScissorRects(1, &scissor);

		cmd.SetGraphicsRootSignature(_rootSignature);
		cmd.SetPipelineState(_pipelineState);
		cmd.IASetPrimitiveTopology(D3DPrimitiveTopology.D3DPrimitiveTopologyTrianglelist);
		cmd.DrawInstanced(3, 1, 0, 0);
	}

	private static ComPtr<ID3D10Blob> Compile(D3DCompiler compiler, byte[] hlsl, string entry, string target)
	{
		var entryUtf8 = Encoding.ASCII.GetBytes(entry + "\0");
		var targetUtf8 = Encoding.ASCII.GetBytes(target + "\0");
		ComPtr<ID3D10Blob> code = default;
		ComPtr<ID3D10Blob> errors = default;
		int hr;
		fixed (byte* pSrc = hlsl)
		fixed (byte* pEntry = entryUtf8)
		fixed (byte* pTarget = targetUtf8)
		{
			hr = compiler.Compile(
				in Unsafe.AsRef<byte>(pSrc), (nuint)hlsl.Length,
				(byte*)null, (D3DShaderMacro*)null, default(ComPtr<ID3DInclude>),
				pEntry, pTarget, 0, 0, ref code, ref errors);
		}
		if (hr < 0)
		{
			var message = errors.Handle is not null ? new string((sbyte*)errors.GetBufferPointer()) : $"HRESULT 0x{hr:X8}";
			throw new InvalidOperationException($"D3DCompile failed for {entry}/{target}: {message}");
		}
		return code;
	}

	public void Dispose()
	{
		_pipelineState.Dispose();
		_rootSignature.Dispose();
	}
}
