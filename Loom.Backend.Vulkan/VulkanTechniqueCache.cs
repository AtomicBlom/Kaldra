using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using Loom.Core.Geometry;
using Loom.Graph;
using Silk.NET.Vulkan;

namespace Loom.Backend.Vulkan;

/// <summary>
/// Bakes graphics pipelines and caches them by a content hash. The key is a SHA-256 over the
/// SPIR-V bytecode plus all pipeline state, so identical techniques bake once and share a VkPipeline,
/// while an edited shader (new bytes) produces a new key and a fresh bake.
/// In-memory. A persistent <c>VkPipelineCache</c> sits behind it, so a pipeline this cache has to bake is still
/// usually assembled from the driver blob a previous run left on disk rather than compiled from scratch.
/// </summary>
public sealed class VulkanTechniqueCache(VulkanContext ctx, PipelineCache pipelineCache = default) : IDisposable
{
	private readonly Dictionary<string, VulkanTechnique> _cache = [];

	public int Count => _cache.Count;

	public VulkanTechnique GetOrBake(in VulkanTechniqueDescription description, string name)
	{
		var spirv = description.Spirv;
		var key = ComputeKey(spirv, description);
		if (_cache.TryGetValue(key, out var cached))
			return cached;

		// `name` is diagnostic-only (the shader-module label) — deliberately not part of the content-hash key.
		var technique = new VulkanTechnique(ctx, description, spirv, name, pipelineCache);
		_cache[key] = technique;
		return technique;
	}

	// SHA-256 over the SPIR-V bytes plus every input that affects the baked pipeline. SHA-256 is
	// stable across runs/machines and collision-resistant; the hex digest is the cache key.
	private static string ComputeKey(ReadOnlySpan<byte> spirv, in VulkanTechniqueDescription description)
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
		// The whole descriptor layout, not just its per-category totals: a binding's slot and span are now part of
		// what the layout *is*, so two shapes that happened to share counts would otherwise collide on one cached
		// pipeline. This also closes a pre-existing hole — the uniform-buffer count was never hashed at all, and was
		// only masked by the SPIR-V bytes differing between any two real shaders.
		AppendBindings(hash, description.Bindings);
		hash.AppendData(BitConverter.GetBytes(description.HasVertexInput));
		hash.AppendData(BitConverter.GetBytes((int)description.DepthFormat));
		hash.AppendData(BitConverter.GetBytes((int)description.Blend));
		hash.AppendData(BitConverter.GetBytes(description.DepthWrite));
		hash.AppendData(BitConverter.GetBytes(description.HasFragmentPush));
		hash.AppendData(BitConverter.GetBytes(description.IsCompute));
		hash.AppendData(BitConverter.GetBytes(description.HasListPush));
		hash.AppendData(BitConverter.GetBytes((int)description.Cull));
		hash.AppendData(BitConverter.GetBytes((int)description.DepthTest));
		var bias = description.Bias ?? default;
		hash.AppendData(BitConverter.GetBytes(description.Bias.HasValue));
		hash.AppendData(BitConverter.GetBytes(bias.Constant));
		hash.AppendData(BitConverter.GetBytes(bias.SlopeScale));
		hash.AppendData(BitConverter.GetBytes(bias.Clamp));
		AppendLayout(hash, description.Layout);
		AppendLayout(hash, description.Instance);
		hash.AppendData(BitConverter.GetBytes(description.Samples));
		hash.AppendData(BitConverter.GetBytes(description.AlphaToCoverage));
		return Convert.ToHexString(hash.GetHashAndReset());
	}

	// Fold the descriptor layout into the content hash: every binding's slot, kind and span, in category order.
	private static void AppendBindings(IncrementalHash hash, in ShaderBindingMap map)
	{
		Append(hash, map.SampledTextures);
		Append(hash, map.StorageImages);
		Append(hash, map.StorageBuffers);
		Append(hash, map.UniformBuffers);

		static void Append(IncrementalHash hash, ImmutableArray<ShaderBinding> category)
		{
			hash.AppendData(BitConverter.GetBytes(category.IsDefault ? 0 : category.Length));
			if (category.IsDefault)
				return;
			foreach (var binding in category)
			{
				hash.AppendData(BitConverter.GetBytes(binding.Index));
				hash.AppendData(BitConverter.GetBytes((int)binding.Kind));
				hash.AppendData(BitConverter.GetBytes(binding.SlotCount));
			}
		}
	}

	// Fold a vertex layout into the content hash so two techniques sharing a shader but differing in layout
	// (per-vertex attributes or the presence/shape of a per-instance stream) bake distinct pipelines.
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

	/// <summary>Dispose every baked pipeline and empty the cache — used by shader hot-reload to force a fresh
	/// bake from the reloaded bytecode. The caller must have drained the GPU first (no pipeline in flight).</summary>
	public void Reset()
	{
		foreach (var technique in _cache.Values)
			technique.Dispose();
		_cache.Clear();
	}

	public void Dispose() => Reset();
}
