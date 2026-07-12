using System.Runtime.CompilerServices;
using System.Text;
using Loom.Graph;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D.Compilers;

namespace Loom.Backend.DirectX12;

/// <summary>
/// Runtime HLSL compilation (via the in-box FXC, <c>D3DCompile</c>) plus shader resolution for the DirectX12 backend.
/// The engine's real techniques are authored in Slang; the Loom build emits a cross-target <c>&lt;name&gt;.hlsl</c>
/// (one combined multi-entry module) + a <c>&lt;name&gt;.json</c> reflection sidecar beside the SPIR-V, and the
/// backend resolves the HLSL here by logical name (the DirectX12 counterpart of resolving embedded SPIR-V). A small
/// set of <see cref="Builtins"/> remains as a fallback for shaders that have no resolvable HLSL yet (e.g. the
/// engine-owned blit/gradient, or a shader not yet cross-targeted). Signed-DXIL via runtime DXC is the follow-up.
/// </summary>
internal static unsafe class DirectX12Shaders
{
	// Engine shaders are embedded in this backend assembly under "<assembly>.Shaders." (the SDK's Embed prefix
	// matches $(AssemblyName)); app shaders are loose under the shader directory. Mirrors Vulkan's resolution.
	private static readonly string EmbeddedPrefix = typeof(DirectX12Shaders).Assembly.GetName().Name + ".Shaders.";

	/// <summary>
	/// Resolve a logical shader name + stage to its build-emitted <em>signed DXIL</em> blob
	/// (<c>&lt;name&gt;.&lt;stage&gt;.dxil</c>, stage = <c>vs</c>/<c>ps</c>/<c>cs</c>), or null if absent. Tries the
	/// engine shaders embedded in this backend assembly first, then the app shaders loose under
	/// <paramref name="shaderDir"/> — the DirectX12 counterpart of Vulkan's embedded-then-disk SPIR-V resolution. The
	/// blob is the offline-built signed DXIL the backend loads straight into the PSO (no runtime compiler shipped).
	/// </summary>
	public static byte[]? TryResolveStageDxil(string shaderDir, string name, string stage)
	{
		var embedded = TryReadEmbedded($"{name}.{stage}.dxil");
		if (embedded is not null)
			return embedded;
		var path = Path.Combine(shaderDir, $"{name}.{stage}.dxil");
		return File.Exists(path) ? File.ReadAllBytes(path) : null;
	}

	private static byte[]? TryReadEmbedded(string suffix)
	{
		var asm = typeof(DirectX12Shaders).Assembly;
		using var stream = asm.GetManifestResourceStream(EmbeddedPrefix + suffix);
		if (stream is null)
			return null;
		var bytes = new byte[stream.Length];
		stream.ReadExactly(bytes);
		return bytes;
	}

	/// <summary>Compile built-in HLSL to a DXBC bytecode blob via the in-box FXC (the fallback stand-in path; ships nothing — d3dcompiler is in Windows).</summary>
	public static byte[] CompileToBytes(string hlsl, string entry, string target)
	{
		using var compiler = D3DCompiler.GetApi();
		var blob = Compile(compiler, Encoding.ASCII.GetBytes(hlsl), entry, target);
		var size = (int)blob.GetBufferSize();
		var bytes = new byte[size];
		new ReadOnlySpan<byte>(blob.GetBufferPointer(), size).CopyTo(bytes);
		blob.Dispose();
		return bytes;
	}

	/// <summary>
	/// The descriptor-bound resource shape of a shader, read from its <c>slangc -reflection-json</c> sidecar — the
	/// counts that size the root signature (SRV table, UAV table, root CBV/SRVs) so the app states only pipeline
	/// intent. Tries the engine sidecar embedded in this backend assembly first, then the app sidecar loose beside
	/// the <c>.dxil</c>. <see cref="ShaderReflection.Empty"/> when there's no sidecar (a built-in stand-in).
	/// </summary>
	public static ShaderReflection ResolveReflection(string shaderDir, string name)
	{
		var json = TryReadEmbedded(name + ".json");
		if (json is null)
		{
			var path = Path.Combine(shaderDir, name + ".json");
			if (!File.Exists(path))
				return ShaderReflection.Empty;
			json = File.ReadAllBytes(path);
		}
		return ShaderReflection.FromBytes(json);
	}

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
		// constant at b0, the DirectX12 counterpart of Vulkan's vertex push constant), and shades each fragment with
		// one directional key light + flat ambient, colouring albedo by normal so each cube face reads distinctly.
		// Proves the mesh-draw path: vertex buffers + index buffer + depth test + the MVP root constant. The
		// matrix math mirrors lit.slang exactly: mul(mvp, position) with the raw row-major Camera.ViewProjectionMatrix
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
		// root UAV at u0 (DirectX12 root descriptors bind a raw buffer by GPU address — no descriptor heap entry), and
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
