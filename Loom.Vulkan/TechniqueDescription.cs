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
/// <see cref="SamplesInput"/> selects the descriptor set layout: <c>true</c> gives a single combined image
/// sampler at set 0/binding 0 (the fullscreen-sample); the per-binding reflection that derives this
/// automatically arrives later.
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
	bool SamplesInput = false,
	bool HasVertexInput = false,
	Format DepthFormat = Format.Undefined);
