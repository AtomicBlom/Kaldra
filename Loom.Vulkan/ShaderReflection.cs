using System.Text.Json;

namespace Loom.Vulkan;

/// <summary>
/// The descriptor-set shape of a shader, read from the <c>slangc -reflection-json</c> sidecar that
/// Loom.Shaders emits next to each <c>.spv</c> (M7-B4; the §10 "Slang's own reflection" decision). It counts
/// how many sampled inputs, storage images, storage buffers, and uniform (constant) buffers the shader binds,
/// so the app no longer hand-counts them on a <see cref="Loom.Core.TechniqueDescriptor"/> —
/// <see cref="VulkanEngine.DefineTechnique"/> derives them here and the bake builds the descriptor-set layout.
/// </summary>
internal readonly record struct ShaderReflection(int SampleCount, int StorageImageCount, int StorageBufferCount, int UniformBufferCount)
{
	/// <summary>
	/// Parse a slangc reflection-JSON file. Only the top-level <c>parameters</c> array is counted — each entry
	/// point re-lists the parameters it uses (with bindings but without the resource type), so counting those
	/// too would double-count. Each <c>descriptorTableSlot</c> parameter is classified by its shape/access:
	/// a <c>constantBuffer</c>-typed slot is a uniform buffer (M15-0 — the per-frame uniform seam), a
	/// structured/byte-address buffer is a storage buffer, a read-write texture is a storage image, and any
	/// other sampled texture is a combined image sampler. Push constants (kind <c>pushConstantBuffer</c>),
	/// varyings, and per-field uniforms are ignored.
	/// </summary>
	public static ShaderReflection FromFile(string jsonPath) => FromBytes(File.ReadAllBytes(jsonPath));

	/// <summary>
	/// Parse slangc reflection JSON held in memory — the engine resolves a shader's reflection either from an
	/// embedded resource (engine shaders) or from disk (app shaders), so the parse takes bytes, not a path.
	/// </summary>
	public static ShaderReflection FromBytes(byte[] json)
	{
		using var doc = JsonDocument.Parse(json);
		int samplers = 0, storageImages = 0, storageBuffers = 0, uniformBuffers = 0;

		if (doc.RootElement.TryGetProperty("parameters", out var parameters) && parameters.ValueKind == JsonValueKind.Array)
		{
			foreach (var p in parameters.EnumerateArray())
			{
				if (!p.TryGetProperty("binding", out var binding)
					|| !binding.TryGetProperty("kind", out var bindingKind)
					|| bindingKind.GetString() != "descriptorTableSlot")
					continue;
				if (!p.TryGetProperty("type", out var type)
					|| !type.TryGetProperty("kind", out var typeKind))
					continue;

				var kind = typeKind.GetString();
				// A descriptor-bound ConstantBuffer<T> — the M15-0 uniform seam (verified against slangc:
				// kind "constantBuffer" in a descriptorTableSlot, distinct from a "pushConstantBuffer" push).
				if (kind == "constantBuffer")
				{
					uniformBuffers++;
					continue;
				}
				if (kind != "resource")
					continue;

				var baseShape = type.TryGetProperty("baseShape", out var shape) ? shape.GetString() : null;
				var readWrite = type.TryGetProperty("access", out var access) && access.GetString() == "readWrite";

				if (baseShape is "structuredBuffer" or "byteAddressBuffer")
					storageBuffers++;
				else if (readWrite)
					storageImages++; // RWTexture* — a storage image
				else
					samplers++;      // a sampled texture, bound as a combined image sampler
			}
		}

		return new ShaderReflection(samplers, storageImages, storageBuffers, uniformBuffers);
	}
}
