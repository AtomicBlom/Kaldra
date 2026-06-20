using System.Security.Cryptography;
using System.Text;
using Loom.Core.Geometry;
using Silk.NET.Direct3D12;
using Silk.NET.DXGI;

namespace Loom.DX12;

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

		var key = BuildKey(name, colorFormats, depthFormat, depthReadOnly, template);
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

	private static string BuildKey(string name, ReadOnlySpan<Format> colorFormats, Format depthFormat, bool depthReadOnly, in DX12TechniqueTemplate template)
	{
		var key = name;
		foreach (var format in colorFormats)
			key += "|" + (int)format;
		key += "|d" + (int)depthFormat + (depthReadOnly ? "r" : "w");
		// Fold the template's content into the key (mirroring the Vulkan cache's content hash) so re-registering a
		// name with different bytecode / state / layout bakes a fresh PSO instead of returning the stale one.
		key += "|t" + TemplateDigest(template);
		return key;
	}

	// SHA-256 over the template's bytecode plus every input that affects the baked PSO — the DX12 counterpart of
	// the Vulkan TechniqueCache content hash.
	private static string TemplateDigest(in DX12TechniqueTemplate t)
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
		hash.AppendData(BitConverter.GetBytes(t.ParticleInput));
		hash.AppendData(BitConverter.GetBytes((int)t.Blend));
		hash.AppendData(BitConverter.GetBytes(t.StorageImages));
		hash.AppendData(BitConverter.GetBytes(t.StorageBuffers));
		hash.AppendData(BitConverter.GetBytes(t.StorageBufferStride));
		hash.AppendData(BitConverter.GetBytes((int)t.Address));
		hash.AppendData(BitConverter.GetBytes(t.UniformBufferCount));
		hash.AppendData(BitConverter.GetBytes(t.InputBufferCount));
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
