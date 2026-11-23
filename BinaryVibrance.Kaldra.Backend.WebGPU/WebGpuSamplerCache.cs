using System.Runtime.Versioning;

using BinaryVibrance.Kaldra.Core.Graph;
using BinaryVibrance.Kaldra.Core.Materials;

namespace BinaryVibrance.Kaldra.Backend.WebGPU;

/// <summary>
/// Lazily creates and caches the small set of samplers techniques need, keyed by (filter, address mode,
/// comparison). WebGPU exposes only three address modes — clamp-to-edge, repeat, mirror-repeat — so the two
/// desktop modes it lacks (clamp-to-border, mirror-clamp-to-edge) fall back to their nearest match; the shadow
/// map is the only user of clamp-to-border and its out-of-frustum samples read the edge texel instead of a
/// border constant, a parity nuance chased per demo rather than a correctness break.
/// </summary>
[SupportedOSPlatform("browser")]
internal sealed class WebGpuSamplerCache(WebGpuHandleAllocator handles) : IDisposable
{
	// Address-mode ids mirrored by the shim's ADDRESS_MODES table.
	public const int ClampToEdge = 0;
	public const int Repeat = 1;
	public const int MirrorRepeat = 2;

	// Compare: 0 for none, else 1 + the DepthCompare (the shim's COMPARE_FUNCTIONS order)
	private readonly Dictionary<(bool Linear, int Address, int Compare), int> _cache = [];

	/// <summary>The sampler handle for a technique's filter + wrap mode (a filtering, non-comparison sampler).</summary>
	public int Get(bool linear, SamplerAddress address) => Get(linear, ToAddressId(address), compare: 0);

	/// <summary>A comparison sampler (a shadow map read with SampleCmp) comparing by <paramref name="compare"/>: always
	/// filtered (the hardware's 2×2 PCF) and clamped to the edge, as on the desktop backends.</summary>
	public int GetComparison(DepthCompare compare) => Get(linear: true, ClampToEdge, compare: 1 + (int)compare);

	private int Get(bool linear, int address, int compare)
	{
		var key = (linear, address, compare);
		if (_cache.TryGetValue(key, out var handle))
			return handle;
		handle = handles.Allocate(WebGpuHandleAllocator.Samplers);
		WebGpuInterop.CreateSampler(handle, linear, address, compare);
		_cache[key] = handle;
		return handle;
	}

	private static int ToAddressId(SamplerAddress address) => address switch
	{
		SamplerAddress.Repeat => Repeat,
		SamplerAddress.MirrorRepeat => MirrorRepeat,
		// clamp-to-border and mirror-clamp-to-edge have no WebGPU equivalent — fall back to clamp-to-edge.
		_ => ClampToEdge,
	};

	public void Dispose()
	{
		foreach (var handle in _cache.Values)
			handles.Release(WebGpuHandleAllocator.Samplers, handle);
		_cache.Clear();
	}
}
