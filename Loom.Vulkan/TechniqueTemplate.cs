namespace Loom.Vulkan;

/// <summary>
/// A named, format-agnostic technique the host registers for the graph to draw with (Planning.md §5's
/// <c>Technique.X</c>, addressed by string until the describe-layer <c>Technique</c> builder lands in M5).
/// It carries everything that identifies the shader, but <em>not</em> the colour format — that is supplied
/// per pass at record time (from the target the pass writes), so one template baked against two targets
/// yields two cached pipelines.
/// </summary>
/// <param name="SpirvPath">Path to the compiled SPIR-V module containing both entry points.</param>
/// <param name="VertexEntry">Vertex entry-point name.</param>
/// <param name="FragmentEntry">Fragment entry-point name.</param>
/// <param name="SamplesInput">True if the fragment samples one input (combined image sampler at set 0/binding 0).</param>
/// <param name="HasVertexInput">True if the vertex stage reads a <see cref="Loom.Core.Vertex"/> buffer (a mesh technique) rather than fabricating a fullscreen triangle.</param>
public readonly record struct TechniqueTemplate(
	string SpirvPath,
	string VertexEntry,
	string FragmentEntry,
	bool SamplesInput = false,
	bool HasVertexInput = false);
