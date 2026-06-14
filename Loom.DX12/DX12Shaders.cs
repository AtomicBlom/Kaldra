using System.Runtime.CompilerServices;
using System.Text;

using Silk.NET.Core.Native;
using Silk.NET.Direct3D.Compilers;

namespace Loom.DX12;

/// <summary>
/// Runtime HLSL→DXBC compilation (via the in-box FXC, <c>D3DCompile</c>) plus the backend's built-in HLSL for
/// the demo techniques. This is the interim shader-resolution path for the DX12 backend: the engine's real
/// techniques are authored in Slang and the proper route is offline Slang→DXIL, but the bundled <c>slangc</c>
/// can't emit DXIL on win-arm64, so until that pipeline lands the executor resolves a small set of built-in
/// HLSL techniques here by logical name. Replaced wholesale when the offline DXIL pipeline arrives.
/// </summary>
internal static unsafe class DX12Shaders
{
	/// <summary>Built-in HLSL + sampled-input count, keyed by the logical shader name a <c>TechniqueDescriptor</c> carries.</summary>
	public static readonly IReadOnlyDictionary<string, (string Hlsl, int InputCount)> Builtins = new Dictionary<string, (string, int)>
	{
		// A from-scratch fullscreen pass: the vertex shader fabricates a screen-filling triangle from
		// SV_VertexID (no vertex buffer) and a UV; the pixel shader writes a UV gradient. Proves a graph-driven
		// raster pass binds its derived render target and runs.
		["gradient"] = ("""
			struct VSOut { float4 pos : SV_Position; float2 uv : TEXCOORD0; };
			VSOut vertexMain(uint vid : SV_VertexID)
			{
				float2 uv = float2((vid << 1) & 2, vid & 2);
				VSOut o;
				o.pos = float4(uv * 2.0 - 1.0, 0.0, 1.0);
				o.uv = uv;
				return o;
			}
			float4 fragmentMain(VSOut i) : SV_Target { return float4(i.uv, 0.5, 1.0); }
			""", 0),

		// A blit/sample pass: samples one input texture (t0, sampler s0) across a fullscreen triangle. Proves
		// the transient + SRV-table + sampler path — an offscreen target written by a prior pass, sampled here.
		["blit"] = ("""
			Texture2D src : register(t0);
			SamplerState samp : register(s0);
			struct VSOut { float4 pos : SV_Position; float2 uv : TEXCOORD0; };
			VSOut vertexMain(uint vid : SV_VertexID)
			{
				float2 uv = float2((vid << 1) & 2, vid & 2);
				VSOut o;
				o.pos = float4(uv * 2.0 - 1.0, 0.0, 1.0);
				o.uv = uv;
				return o;
			}
			float4 fragmentMain(VSOut i) : SV_Target { return src.Sample(samp, i.uv); }
			""", 1),
	};

	public static ComPtr<ID3D10Blob> Compile(D3DCompiler compiler, byte[] hlsl, string entry, string target)
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
}
