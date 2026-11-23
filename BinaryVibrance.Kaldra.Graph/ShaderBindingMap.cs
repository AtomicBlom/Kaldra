using System.Collections.Immutable;
using BinaryVibrance.Kaldra.Core.Graph;

namespace BinaryVibrance.Kaldra.Graph;

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

	/// <summary>
	/// Refuse a draw technique (one that isn't compute) whose shader declares read-write storage: a <c>RWStructuredBuffer</c>
	/// or a <c>RWTexture</c>. A draw binds only the storage its pass <c>.Reads</c>, read-only, so no backend has anything
	/// to put in such a slot, and each would leave it unbound in its own way. Checked when the technique is defined, the
	/// same on every backend.
	/// </summary>
	public void CheckDrawStorage(string technique, bool isCompute)
	{
		if (isCompute)
			return;
		if (!StorageImages.IsDefaultOrEmpty)
			throw new InvalidOperationException(
				$"Technique '{technique}' is a draw technique and its shader declares {StorageImages.Length} read-write texture(s) " +
				$"('{StorageImages[0].Name}' first). A draw binds no storage images; write them from a compute pass.");
		if (StorageBuffers.IsDefaultOrEmpty)
			return;
		foreach (var binding in StorageBuffers)
		{
			if (binding.Kind == ShaderBindingKind.StorageBufferReadOnly)
				continue;
			throw new InvalidOperationException(
				$"Technique '{technique}' is a draw technique and its shader declares read-write storage buffer '{binding.Name}'. " +
				"A draw binds only the storage its pass .Reads, read-only: declare it StructuredBuffer<T>, or write it from a compute pass.");
		}
	}

	/// <summary>
	/// Hold the storage buffers <paramref name="pass"/> <c>.Reads</c> to the technique's storage slots they bind to. The
	/// binding is positional: the technique's <c>k</c>-th storage slot (with <paramref name="readOnlyOnly"/>, its
	/// <c>k</c>-th read-only one, as a compute technique's read-write slots take its dispatch's buffers) takes the pass's
	/// <c>k</c>-th <c>.Reads</c> storage buffer, from the first. So a technique that declares only a later one of a pass's
	/// buffers reads an earlier one in its place; where the element sizes show it, this throws rather than shading from the
	/// wrong buffer. A slot or buffer of unknown element size passes, and the counts are each backend's own check.
	/// Allocation-free, for the draw path.
	/// </summary>
	public void CheckPassStorage(string technique, GraphPass pass, bool readOnlyOnly = false)
	{
		if (StorageBuffers.IsDefaultOrEmpty)
			return;
		int slot = 0, read = 0;
		foreach (var access in pass.Accesses)
		{
			if (access.Kind != AccessKind.ReadStorage)
				continue;
			read++;
			while (readOnlyOnly && slot < StorageBuffers.Length && StorageBuffers[slot].Kind != ShaderBindingKind.StorageBufferReadOnly)
				slot++;
			if (slot >= StorageBuffers.Length)
				return;
			var binding = StorageBuffers[slot++];
			if (binding.AcceptsElementSize(access.Resource.Stride))
				continue;
			throw new InvalidOperationException(
				$"Technique '{technique}' reads storage buffer '{binding.Name}' as {binding.ElementStride}-byte elements, but in pass " +
				$"'{pass.Name}' it binds to the storage buffer the pass .Reads #{read}, '{access.Resource.Name}', of " +
				$"{access.Resource.Stride}-byte elements. A technique's storage buffers take the pass's .Reads storage in order, its first the first the pass " +
				"reads, so declare them in the shader in the order the pass reads them, from its first.");
		}
	}

	/// <summary>
	/// Hold a compute dispatch's own storage to the technique's read-write slots: its images to the storage images the
	/// shader declares, and its buffers to the read-write storage buffers (the read-only ones take the pass's <c>.Reads</c>
	/// storage, <see cref="CheckPassStorage"/>). Throws where the counts differ, on every backend alike: a surplus resource
	/// would be bound nowhere and a missing one would leave a slot unbound. Allocation-free, for the dispatch path.
	/// </summary>
	public void CheckDispatchStorage(string technique, ReadOnlySpan<ResourceHandle> storage)
	{
		int images = 0, buffers = 0;
		foreach (var handle in storage)
		{
			if (handle.Kind == ResourceKind.Buffer)
				buffers++;
			else
				images++;
		}
		var declaredImages = StorageImages.IsDefault ? 0 : StorageImages.Length;
		var declaredBuffers = 0;
		if (!StorageBuffers.IsDefault)
		{
			foreach (var binding in StorageBuffers)
			{
				if (binding.Kind != ShaderBindingKind.StorageBufferReadOnly)
					declaredBuffers++;
			}
		}
		if (images != declaredImages)
			throw new InvalidOperationException(
				$"Compute technique '{technique}' declares {declaredImages} storage image(s) but its dispatch supplies {images}. " +
				"A dispatch's storage images fill the shader's RWTexture slots in order, one each.");
		if (buffers != declaredBuffers)
			throw new InvalidOperationException(
				$"Compute technique '{technique}' declares {declaredBuffers} read-write storage buffer(s) but its dispatch supplies {buffers}. " +
				"A dispatch's storage buffers fill the shader's RWStructuredBuffer slots in order, one each; its read-only " +
				"StructuredBuffers take the pass's .Reads storage.");
	}
}
