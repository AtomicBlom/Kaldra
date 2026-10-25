using Loom.Core.Geometry;
using Loom.Core.Graph;
using Loom.Graph;
using Format = Silk.NET.Vulkan.Format;

namespace Loom.Backend.Vulkan;

/// <summary>
/// The bake inputs that uniquely identify a graphics <see cref="VulkanTechnique"/> — the cache key.
/// As real pipeline state (blend, depth, cull, topology, vertex/instance layout) becomes
/// configurable, it joins this record and the hash in <see cref="VulkanTechniqueCache"/>.
/// <para>
/// <see cref="ColorFormats"/> are filled in at record time from the formats of the targets the pass writes,
/// in attachment order (dynamic rendering requires the pipeline's attachment formats to match; a g-buffer
/// pass has several), so one shader baked against different targets yields different cached pipelines.
/// <see cref="Bindings"/> is the descriptor set layout: each entry carries the slot the shader declared, its kind,
/// and how many slots it spans, so the layout is what the shader asked for rather than a category order the author
/// had to hand-number to match.
/// </para>
/// <para>
/// The <see cref="VulkanTechniqueCache"/> keys on a content hash (which folds in every field here), not this
/// record's value equality — so the <see cref="ColorFormats"/> array reference is never compared directly.
/// </para>
/// </summary>
public readonly record struct VulkanTechniqueDescription(
	byte[] Spirv,
	string VertexEntry,
	string FragmentEntry,
	Format[] ColorFormats,
	ShaderBindingMap Bindings = default,
	bool HasVertexInput = false,
	Format DepthFormat = Format.Undefined,
	BlendMode Blend = BlendMode.None,
	bool DepthWrite = true,
	bool HasFragmentPush = false,
	bool IsCompute = false,
	VertexLayout? Layout = null,
	VertexLayout? Instance = null,
	bool HasListPush = false,
	CullMode Cull = CullMode.None,
	DepthCompare DepthTest = DepthCompare.LessOrEqual,
	DepthBias? Bias = null)
{
	/// <summary>Sampled inputs the shader reads, bound as combined image samplers.</summary>
	public int SampleCount => Bindings.SampledTextures.Length;

	/// <summary>Storage images the (compute) shader read-writes.</summary>
	public int StorageCount => Bindings.StorageImages.Length;

	/// <summary>Storage buffers (not images) the shader binds.</summary>
	public int StorageBufferCount => Bindings.StorageBuffers.Length;

	/// <summary>Descriptor-bound constant buffers the shader reads.</summary>
	public int UniformBufferCount => Bindings.UniformBuffers.Length;
}
