using Silk.NET.Direct3D.Compilers;
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
