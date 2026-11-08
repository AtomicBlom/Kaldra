using System.Collections.Immutable;
using Loom.Core.Graph;

namespace Loom.Graph;

/// <summary>
/// A shader's bindings grouped by the category the record path fills, each ascending by index: element <c>k</c> of
/// <see cref="SampledTextures"/> is the slot the pass's <c>k</c>-th sampled input binds to, element <c>k</c> of
/// <see cref="UniformBuffers"/> is uniform slot <c>k</c>, and so on.
/// <para>
/// This exists so a technique's descriptor layout and the code that writes descriptors into it read the <em>same</em>
/// array. Both used to recompute "N samplers, then M storage images, then…" from counts, independently — which is
/// how a layout and its writes could come to disagree about where a binding lived without anything failing to
/// compile.
/// </para>
/// </summary>
public readonly record struct ShaderBindingMap
{
	private ShaderBindingMap(
		ImmutableArray<ShaderBinding> sampledTextures,
		ImmutableArray<ShaderBinding> storageImages,
		ImmutableArray<ShaderBinding> storageBuffers,
		ImmutableArray<ShaderBinding> uniformBuffers,
		ShaderBindingTarget target,
		ImmutableArray<bool> comparedTextures)
	{
		SampledTextures = sampledTextures;
		ComparedTextures = comparedTextures;
		foreach (var compared in comparedTextures)
		{
			if (compared)
				ComparedCount++;
		}
		StorageImages = storageImages;
		StorageBuffers = storageBuffers;
		UniformBuffers = uniformBuffers;
		Target = target;
	}

	/// <summary>Read-only textures, ascending by index.</summary>
	public ImmutableArray<ShaderBinding> SampledTextures { get; }

	/// <summary>
	/// Whether each of <see cref="SampledTextures"/> is read by comparison (a shadow sampler), element for element:
	/// such a slot takes a comparison sampler with the technique's <c>TechniqueDescriptor.SampleCompare</c>. Found in
	/// the shader's SPIR-V (<see cref="SpirvDepthTextures"/>); all false when none was given.
	/// </summary>
	public ImmutableArray<bool> ComparedTextures { get; }

	/// <summary>How many sampled textures are read by comparison. Counted once, so a draw can ask it without allocating.</summary>
	public int ComparedCount { get; }

	/// <summary>Read-write textures, ascending by index.</summary>
	public ImmutableArray<ShaderBinding> StorageImages { get; }

	/// <summary>
	/// Structured and byte-address buffers, read-only and read-write together, ascending by index — one sequence
	/// because a pass supplies its storage buffers as one list and the k-th of them binds to the k-th slot here.
	/// The read-only/read-write split stays legible through each entry's <see cref="ShaderBinding.Kind"/>.
	/// </summary>
	public ImmutableArray<ShaderBinding> StorageBuffers { get; }

	/// <summary>Constant buffers, ascending by index — the order uniform slots are numbered in.</summary>
	public ImmutableArray<ShaderBinding> UniformBuffers { get; }

	/// <summary>Which target's numbering these indices are in. A backend must check this before building a layout.</summary>
	public ShaderBindingTarget Target { get; }

	/// <summary>Group <paramref name="reflection"/>'s bindings by category, preserving index order within each; the
	/// sampled textures at the bindings in <paramref name="compared"/> are read by comparison.</summary>
	public static ShaderBindingMap From(in ShaderReflection reflection, IReadOnlySet<int>? compared = null)
	{
		if (reflection.Bindings.IsDefaultOrEmpty)
			return new ShaderBindingMap([], [], [], [], reflection.Target, []);

		var sampled = ImmutableArray.CreateBuilder<ShaderBinding>();
		var storageImages = ImmutableArray.CreateBuilder<ShaderBinding>();
		var storageBuffers = ImmutableArray.CreateBuilder<ShaderBinding>();
		var uniforms = ImmutableArray.CreateBuilder<ShaderBinding>();

		// Bindings arrive index-sorted, so appending preserves ascending order per category for free.
		foreach (var binding in reflection.Bindings)
		{
			switch (binding.Kind)
			{
				case ShaderBindingKind.SampledTexture: sampled.Add(binding); break;
				case ShaderBindingKind.StorageImage: storageImages.Add(binding); break;
				case ShaderBindingKind.StorageBufferReadOnly:
				case ShaderBindingKind.StorageBufferReadWrite: storageBuffers.Add(binding); break;
				case ShaderBindingKind.UniformBuffer: uniforms.Add(binding); break;
				default: throw new ArgumentOutOfRangeException(nameof(reflection), binding.Kind, "Unmapped shader binding kind.");
			}
		}

		var sampledTextures = sampled.ToImmutable();
		return new ShaderBindingMap(
			sampledTextures, storageImages.ToImmutable(), storageBuffers.ToImmutable(), uniforms.ToImmutable(),
			reflection.Target, [.. sampledTextures.Select(t => compared?.Contains(t.Index) == true)]);
	}

	/// <summary>
	/// A technique's comparison function matched against what its shader reads by comparison: one without the other is
	/// a sampler that can't work, so it throws with both named.
	/// </summary>
	public static void CheckCompare(string technique, int comparedCount, DepthCompare? sampleCompare)
	{
		if (comparedCount > 0 && sampleCompare is null)
			throw new InvalidOperationException(
				$"Technique '{technique}' reads {comparedCount} texture(s) by comparison (SampleCmp, a shadow sampler) but names no " +
				"TechniqueDescriptor.SampleCompare, the function its comparison samplers compare with.");
		if (comparedCount == 0 && sampleCompare is not null)
			throw new InvalidOperationException(
				$"Technique '{technique}' names SampleCompare {sampleCompare} but its shader reads no texture by comparison " +
				"(SampleCmp on a Sampler2DShadow).");
	}
}
