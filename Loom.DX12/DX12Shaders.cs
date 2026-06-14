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

		// Forward directional lighting — the HLSL twin of the sandbox's lit.slang. Reads the canonical
		// Loom.Core.Vertex stream (POSITION/NORMAL/TEXCOORD), transforms by the camera MVP (a 64-byte root
		// constant at b0, the DX12 counterpart of Vulkan's vertex push constant), and shades each fragment with
		// one directional key light + flat ambient, colouring albedo by normal so each cube face reads distinctly.
		// Proves the mesh-draw path: vertex buffers + index buffer + depth test + the MVP root constant. The
		// matrix math mirrors lit.slang exactly: mul(mvp, position) with the raw row-major Camera.ViewProjection
		// (FXC's column-major cbuffer packing transposes it, yielding the row-vector clip = position·MVP).
		["lit"] = ("""
			cbuffer Push : register(b0) { float4x4 mvp; };
			struct VsIn { float3 position : POSITION; float3 normal : NORMAL; float2 uv : TEXCOORD0; };
			struct VsOut { float4 position : SV_Position; float3 normal : NORMAL; float2 uv : TEXCOORD0; };
			VsOut vertexMain(VsIn input)
			{
				VsOut o;
				o.position = mul(mvp, float4(input.position, 1.0));
				o.normal = input.normal;
				o.uv = input.uv;
				return o;
			}
			float4 fragmentMain(VsOut input) : SV_Target
			{
				float3 n = normalize(input.normal);
				float3 albedo = n * 0.5 + 0.5;                        // distinct per-face colour
				float3 lightDir = normalize(float3(0.6, 0.55, 0.45)); // matches the sandbox sun
				float ndotl = max(dot(n, lightDir), 0.0);
				const float ambient = 0.15;
				const float key = 1.5;                                // > 1 so lit faces are HDR (clamped on an SDR target)
				return float4(albedo * (ambient + ndotl * key), 1.0);
			}
			""", 0),

		// GPU particle update — the HLSL twin of particle_update.slang. A COMPUTE shader that writes a storage
		// buffer (one float4 per particle: xyz position + w life). The buffer is a RWByteAddressBuffer bound as a
		// root UAV at u0 (DX12 root descriptors bind a raw buffer by GPU address — no descriptor heap entry), and
		// the per-pass scalars ride a root constant at b0 (p.x = time, p.y = particle count). Positions are a pure
		// function of (index, time) — a looping fountain — so no cross-frame state. Proves Dispatch + the UAV.
		["particle_update"] = ("""
			RWByteAddressBuffer particles : register(u0);
			cbuffer Push : register(b0) { float4 p; };
			[numthreads(64, 1, 1)]
			void computeMain(uint3 id : SV_DispatchThreadID)
			{
				uint i = id.x;
				uint count = (uint)p.y;
				if (i >= count)
					return;
				float time = p.x;
				float phase = frac(time * 0.22 + i * (1.0 / count));
				float angle = i * 2.39996323 + time * 0.6;     // golden-angle spread + slow swirl
				float spread = 0.15 + phase * 0.9;             // widens as it rises
				float height = -0.45 + phase * 3.0;            // rises from the cube base
				float4 v = float4(cos(angle) * spread, height, sin(angle) * spread, 1.0 - phase);
				particles.Store4(i * 16, asuint(v));
			}
			""", 0),

		// GPU particle render — the HLSL twin of particle_render.slang. Each particle is one INSTANCE; the buffer
		// is bound as the per-instance vertex stream (one float4: xyz position + w life). The vertex stage
		// fabricates a 6-vertex billboard quad sized in clip space; the fragment makes a soft round emissive spark
		// (additively blended). MVP rides a root constant at b0/vertex.
		["particle_render"] = ("""
			cbuffer Push : register(b0) { float4x4 mvp; };
			struct VsOut { float4 position : SV_Position; float2 offset : TEXCOORD0; float life : TEXCOORD1; };
			VsOut vertexMain(float4 particle : POSITION, uint vid : SV_VertexID)
			{
				const float2 corners[6] = {
					float2(-1, -1), float2(1, -1), float2(1, 1),
					float2(-1, -1), float2(1, 1), float2(-1, 1),
				};
				float2 corner = corners[vid];
				float4 clip = mul(mvp, float4(particle.xyz, 1.0));
				const float size = 0.012;
				clip.xy += corner * size * clip.w;            // constant on-screen size with depth
				VsOut o;
				o.position = clip;
				o.offset = corner;
				o.life = particle.w;
				return o;
			}
			float4 fragmentMain(VsOut input) : SV_Target
			{
				float d = length(input.offset);
				float falloff = saturate(1.0 - d);
				falloff *= falloff;                            // soft round dot
				float3 colour = float3(2.6, 1.6, 0.5) * input.life * falloff; // emissive warm spark
				return float4(colour, 1.0);
			}
			""", 0),
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
