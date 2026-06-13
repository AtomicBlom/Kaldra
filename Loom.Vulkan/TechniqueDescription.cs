using Silk.NET.Vulkan;

namespace Loom.Vulkan;

/// <summary>
/// The bake inputs that uniquely identify a graphics <see cref="Technique"/> — the cache key (M1).
/// As real pipeline state (blend, depth, cull, topology, vertex/instance layout) becomes
/// configurable, it joins this record and the hash in <see cref="TechniqueCache"/>.
/// <para>
/// <see cref="ColorFormat"/> is filled in at record time from the format of the target the pass writes
/// (dynamic rendering requires the pipeline's attachment format to match), so one shader baked against
/// two formats yields two cached pipelines. <see cref="SamplesInput"/> selects the descriptor set layout:
/// <c>true</c> gives a single combined image sampler at set 0/binding 0 (the M3 fullscreen-sample); the
/// per-binding reflection that derives this automatically arrives later.
/// </para>
/// </summary>
public readonly record struct TechniqueDescription(
	string SpirvPath,
	string VertexEntry,
	string FragmentEntry,
	Format ColorFormat,
	bool SamplesInput = false,
	bool HasVertexInput = false);
