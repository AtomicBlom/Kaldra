using Loom.Core;
using Loom.Core.Geometry;
using Loom.Core.Graph;
using Loom.Core.Materials;

namespace Loom.Vulkan;

/// <summary>
/// A named, format-agnostic technique the host registers for the graph to draw with (Planning.md §5's
/// <c>VulkanTechnique.X</c>, addressed by string until the describe-layer <c>VulkanTechnique</c> builder lands in M5).
/// It carries everything that identifies the shader, but <em>not</em> the colour format — that is supplied
/// per pass at record time (from the target the pass writes), so one template baked against two targets
/// yields two cached pipelines.
/// </summary>
/// <param name="Spirv">The compiled SPIR-V module containing both entry points, resolved once at
/// <see cref="VulkanEngine.DefineTechnique"/> — from an embedded resource (engine shaders) or disk (app shaders).</param>
/// <param name="VertexEntry">Vertex entry-point name.</param>
/// <param name="FragmentEntry">Fragment entry-point name.</param>
/// <param name="SampleCount">Number of sampled inputs the fragment reads (combined image samplers at set 0, bindings 0..N-1).</param>
/// <param name="HasVertexInput">True if the vertex stage reads a <see cref="Vertex"/> buffer (a mesh technique) rather than fabricating a fullscreen triangle.</param>
/// <param name="Blend">The blend equation — pairs with a pass that <c>.Blends</c> its target (alpha-over for transparency, additive for bloom).</param>
/// <param name="LinearFilter">Sample inputs with a linear (rather than nearest) sampler — smooth filtering for bloom's downsample/upsample; nearest elsewhere keeps the pixel grid crisp.</param>
/// <param name="HasFragmentPush">True if the shader's fragment (or, for a compute technique, compute) stage reads a small push-constant block (up to four floats) — the per-pass scalar channel (JFA step, hovered id, exposure rate).</param>
/// <param name="IsCompute">True for a compute technique: one compute entry point (in <see cref="FragmentEntry"/>), dispatched rather than drawn (Planning.md §5 <c>.Dispatch</c>).</param>
/// <param name="StorageCount">Number of storage images the (compute) shader read-writes, bound after the sampled inputs (bindings SampleCount..SampleCount+StorageCount-1).</param>
/// <param name="UniformBufferCount">Number of descriptor-bound constant buffers the shader reads (M15-0 — the per-frame uniform seam), bound after the storage slots; 0 or 1 in practice. Fed from the per-frame uniform ring; carries data too large for the 16-float push (CSM matrices, lighting params, wind).</param>
public readonly record struct VulkanTechniqueTemplate(
	byte[] Spirv,
	string VertexEntry,
	string FragmentEntry,
	int SampleCount = 0,
	bool HasVertexInput = false,
	BlendMode Blend = BlendMode.None,
	bool LinearFilter = false,
	bool HasFragmentPush = false,
	bool IsCompute = false,
	int StorageCount = 0,
	int StorageBufferCount = 0,
	bool ParticleInput = false,
	SamplerAddress Address = SamplerAddress.Clamp,
	VertexLayout? Layout = null,
	VertexLayout? Instance = null,
	int UniformBufferCount = 0);
