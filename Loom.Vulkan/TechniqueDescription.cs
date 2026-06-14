using Silk.NET.Vulkan;

namespace Loom.Vulkan;

/// <summary>
/// The bake inputs that uniquely identify a graphics <see cref="Technique"/> — the cache key (M1).
/// As real pipeline state (blend, depth, cull, topology, vertex/instance layout) becomes
/// configurable, it joins this record and the hash in <see cref="TechniqueCache"/>.
/// <para>
/// <see cref="ColorFormats"/> are filled in at record time from the formats of the targets the pass writes,
/// in attachment order (dynamic rendering requires the pipeline's attachment formats to match; a g-buffer
/// pass has several), so one shader baked against different targets yields different cached pipelines.
/// <see cref="SampleCount"/> selects the descriptor set layout: N gives N combined image samplers at set 0,
/// bindings 0..N-1 (the fullscreen-sample / deferred-lighting inputs); the per-binding reflection that derives
/// this automatically arrives later.
/// </para>
/// <para>
/// The <see cref="TechniqueCache"/> keys on a content hash (which folds in every field here), not this
/// record's value equality — so the <see cref="ColorFormats"/> array reference is never compared directly.
/// </para>
/// </summary>
public readonly record struct TechniqueDescription(
	string SpirvPath,
	string VertexEntry,
	string FragmentEntry,
	Format[] ColorFormats,
	int SampleCount = 0,
	bool HasVertexInput = false,
	Format DepthFormat = Format.Undefined,
	BlendMode Blend = BlendMode.None,
	bool DepthWrite = true,
	bool HasFragmentPush = false,
	bool IsCompute = false,
	int StorageCount = 0,
	int StorageBufferCount = 0,
	bool ParticleInput = false);
