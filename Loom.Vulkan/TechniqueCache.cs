using System.Security.Cryptography;
using System.Text;

namespace Loom.Vulkan;

/// <summary>
/// Bakes graphics pipelines and caches them by a content hash (Planning.md §2, §10). The key is
/// a SHA-256 over the SPIR-V bytecode plus all pipeline state, so identical techniques bake once
/// and share a VkPipeline, while an edited shader (new bytes) produces a new key and a fresh bake.
/// In-memory for now; the same hashing scheme will key a persistent VkPipelineCache later.
/// </summary>
public sealed class TechniqueCache(VulkanContext ctx) : IDisposable
{
	private readonly Dictionary<string, Technique> _cache = [];

	public int Count => _cache.Count;

	public Technique GetOrBake(in TechniqueDescription description)
	{
		var spirv = File.ReadAllBytes(description.SpirvPath);
		var key = ComputeKey(spirv, description);
		if (_cache.TryGetValue(key, out var cached))
			return cached;

		var technique = new Technique(ctx, description, spirv);
		_cache[key] = technique;
		return technique;
	}

	// SHA-256 over the SPIR-V bytes plus every input that affects the baked pipeline. SHA-256 is
	// stable across runs/machines and collision-resistant; the hex digest is the cache key.
	private static string ComputeKey(ReadOnlySpan<byte> spirv, in TechniqueDescription description)
	{
		using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
		hash.AppendData(spirv);
		hash.AppendData(Encoding.UTF8.GetBytes(description.VertexEntry));
		hash.AppendData("\0"u8);
		hash.AppendData(Encoding.UTF8.GetBytes(description.FragmentEntry));
		hash.AppendData("\0"u8);
		hash.AppendData(BitConverter.GetBytes(description.ColorFormats.Length));
		foreach (var format in description.ColorFormats)
			hash.AppendData(BitConverter.GetBytes((int)format));
		hash.AppendData(BitConverter.GetBytes(description.SampleCount));
		hash.AppendData(BitConverter.GetBytes(description.HasVertexInput));
		hash.AppendData(BitConverter.GetBytes((int)description.DepthFormat));
		hash.AppendData(BitConverter.GetBytes(description.Blend));
		hash.AppendData(BitConverter.GetBytes(description.DepthWrite));
		return Convert.ToHexString(hash.GetHashAndReset());
	}

	public void Dispose()
	{
		foreach (var technique in _cache.Values)
			technique.Dispose();
		_cache.Clear();
	}
}
