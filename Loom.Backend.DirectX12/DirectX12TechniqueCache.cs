using System.Security.Cryptography;
using System.Text;
using Loom.Core.Geometry;
using Loom.Core.Hosting;
using Silk.NET.Direct3D12;
using Silk.NET.DXGI;

namespace Loom.Backend.DirectX12;

/// <summary>
/// Bakes and caches DirectX12 pipelines per technique — the counterpart of the Vulkan backend's
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
internal sealed unsafe class DirectX12TechniqueCache : IDisposable
{
	/// <summary>The most colour targets a D3D12 pass can bind (<c>D3D12_SIMULTANEOUS_RENDER_TARGET_COUNT</c>).</summary>
	private const int MaxColorTargets = 8;

	private readonly DirectX12Context _ctx;
	private readonly Dictionary<string, RegisteredTemplate> _templates = [];
	private readonly Dictionary<BakeKey, DirectX12Technique> _baked = [];

	// A registered template plus the content digest of it, computed once here rather than per lookup — the digest is
	// a SHA-256 over the whole DXIL, and GetOrBake runs on the per-draw path.
	private readonly record struct RegisteredTemplate(DirectX12TechniqueTemplate Template, string Digest);

	// The identity of a baked PSO: the logical name, the digest of the template it was baked from (so re-registering
	// a name with new bytecode bakes fresh instead of returning the stale PSO), and the exact target formats the PSO
	// is compiled against. A struct key, because looking one up happens once per draw and must not allocate.
	private readonly record struct BakeKey(string Name, string Digest, ulong ColorFormats, int ColorCount, Format DepthFormat, bool DepthReadOnly);

	public DirectX12TechniqueCache(DirectX12Context ctx) => _ctx = ctx;

	public void Register(string name, DirectX12TechniqueTemplate template) =>
		_templates[name] = new RegisteredTemplate(template, TemplateDigest(template));

	public DirectX12Technique GetOrBake(string name, ReadOnlySpan<Format> colorFormats, Format depthFormat, bool depthReadOnly)
	{
		if (!_templates.TryGetValue(name, out var registered))
			throw new TechniqueNotDefinedException(name);

		var key = new BakeKey(name, registered.Digest, PackFormats(colorFormats), colorFormats.Length, depthFormat, depthReadOnly);
		if (_baked.TryGetValue(key, out var cached))
			return cached;

		var technique = new DirectX12Technique(_ctx, registered.Template, colorFormats, depthFormat, depthReadOnly);
		// Name the baked PSO + root signature for GPU debuggers (DirectX12 has no separate shader objects — the DXIL
		// lives in the PSO, so naming the PSO is the shader-naming equivalent). [Conditional]: stripped without
		// UseNamedGPUObjects. Distinct target-format variants of one technique share the logical name.
		_ctx.SetObjectName((ID3D12Object*)technique.Pipeline.Handle, $"pipeline:{name}");
		_ctx.SetObjectName((ID3D12Object*)technique.RootSignature.Handle, $"rootsig:{name}");
		_baked[key] = technique;
		return technique;
	}

	// A pass's colour formats packed one byte per target — exact (every DXGI format value fits a byte), so the key
	// distinguishes PSO variants without hashing and without allocating. Both bounds throw rather than fold, since
	// silently colliding two format sets would hand a pass a PSO compiled for different targets.
	private static ulong PackFormats(ReadOnlySpan<Format> colorFormats)
	{
		if (colorFormats.Length > MaxColorTargets)
			throw new NotSupportedException($"DirectX12: a pass binds {colorFormats.Length} colour targets; D3D12 allows at most {MaxColorTargets}.");

		var packed = 0UL;
		for (var i = 0; i < colorFormats.Length; i++)
		{
			var value = (uint)colorFormats[i];
			if (value > byte.MaxValue)
				throw new NotSupportedException($"DirectX12: DXGI format {colorFormats[i]} ({value}) does not fit the pipeline cache key's byte-per-target packing.");
			packed |= (ulong)value << (8 * i);
		}
		return packed;
	}

	// SHA-256 over the template's bytecode plus every input that affects the baked PSO — the DirectX12 counterpart of
	// the Vulkan TechniqueCache content hash.
	private static string TemplateDigest(in DirectX12TechniqueTemplate t)
	{
		using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
		hash.AppendData(t.VertexBytecode);
		hash.AppendData(t.FragmentBytecode);
		hash.AppendData(Encoding.UTF8.GetBytes(t.VertexEntry));
		hash.AppendData("\0"u8);
		hash.AppendData(Encoding.UTF8.GetBytes(t.FragmentEntry));
		hash.AppendData(BitConverter.GetBytes(t.InputCount));
		hash.AppendData(BitConverter.GetBytes(t.HasVertexInput));
		hash.AppendData(BitConverter.GetBytes(t.HasFragmentPush));
		hash.AppendData(BitConverter.GetBytes(t.LinearFilter));
		hash.AppendData(BitConverter.GetBytes(t.IsCompute));
		hash.AppendData(BitConverter.GetBytes((int)t.Blend));
		hash.AppendData(BitConverter.GetBytes(t.StorageImages));
		hash.AppendData(BitConverter.GetBytes(t.StorageBuffers));
		hash.AppendData(BitConverter.GetBytes(t.StorageBufferStride));
		hash.AppendData(BitConverter.GetBytes((int)t.Address));
		hash.AppendData(BitConverter.GetBytes(t.UniformBufferCount));
		hash.AppendData(BitConverter.GetBytes(t.InputBufferCount));
		hash.AppendData(BitConverter.GetBytes(t.HasListPush));
		hash.AppendData(BitConverter.GetBytes((int)t.Cull));
		hash.AppendData(BitConverter.GetBytes((int)t.DepthTest));
		var bias = t.Bias ?? default;
		hash.AppendData(BitConverter.GetBytes(t.Bias.HasValue));
		hash.AppendData(BitConverter.GetBytes(bias.Constant));
		hash.AppendData(BitConverter.GetBytes(bias.SlopeScale));
		hash.AppendData(BitConverter.GetBytes(bias.Clamp));
		AppendLayout(hash, t.Layout);
		AppendLayout(hash, t.Instance);
		return Convert.ToHexString(hash.GetHashAndReset());
	}

	private static void AppendLayout(IncrementalHash hash, VertexLayout? layout)
	{
		if (layout is null)
		{
			hash.AppendData(BitConverter.GetBytes(-1));
			return;
		}
		hash.AppendData(BitConverter.GetBytes((int)layout.InputRate));
		hash.AppendData(BitConverter.GetBytes(layout.Binding));
		hash.AppendData(BitConverter.GetBytes(layout.Stride));
		foreach (var attribute in layout.Attributes)
			hash.AppendData(BitConverter.GetBytes((int)attribute.Format));
	}

	/// <summary>Dispose every baked PSO and empty the bake cache — used by shader hot-reload to force a fresh
	/// bake from re-registered templates. Templates are kept (re-registered by DefineTechnique). Drain the GPU first.</summary>
	public void Reset()
	{
		foreach (var technique in _baked.Values)
			technique.Dispose();
		_baked.Clear();
	}

	public void Dispose() => Reset();
}
