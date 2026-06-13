using Silk.NET.Vulkan;

namespace Loom.Vulkan;

/// <summary>
/// The bake inputs that uniquely identify a graphics <see cref="Technique"/> — the cache key (M1).
/// As real pipeline state (blend, depth, cull, topology, vertex/instance layout) becomes
/// configurable in M2, it joins this record and the hash in <see cref="TechniqueCache"/>.
/// </summary>
public readonly record struct TechniqueDescription(
	string SpirvPath,
	string VertexEntry,
	string FragmentEntry,
	Format ColorFormat);
