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
/// <param name="SampleCount">Number of sampled inputs the fragment reads (combined image samplers at set 0, bindings 0..N-1).</param>
/// <param name="HasVertexInput">True if the vertex stage reads a <see cref="Loom.Core.Vertex"/> buffer (a mesh technique) rather than fabricating a fullscreen triangle.</param>
/// <param name="Blend">The blend equation — pairs with a pass that <c>.Blends</c> its target (alpha-over for transparency, additive for bloom).</param>
/// <param name="LinearFilter">Sample inputs with a linear (rather than nearest) sampler — smooth filtering for bloom's downsample/upsample; nearest elsewhere keeps the pixel grid crisp.</param>
/// <param name="HasFragmentPush">True if the fragment stage reads a small push-constant block (up to four floats) — the per-pass scalar channel (JFA step, hovered id, outline width/glow).</param>
public readonly record struct TechniqueTemplate(
	string SpirvPath,
	string VertexEntry,
	string FragmentEntry,
	int SampleCount = 0,
	bool HasVertexInput = false,
	BlendMode Blend = BlendMode.None,
	bool LinearFilter = false,
	bool HasFragmentPush = false);
