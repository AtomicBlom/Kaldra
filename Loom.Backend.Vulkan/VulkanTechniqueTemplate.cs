using Loom.Core.Geometry;
using Loom.Core.Graph;
using Loom.Core.Materials;
using Loom.Graph;

namespace Loom.Backend.Vulkan;

/// <summary>
/// A named, format-agnostic technique the host registers for the graph to draw with.
/// It carries everything that identifies the shader, but <em>not</em> the colour format — that is supplied
/// per pass at record time (from the target the pass writes), so one template baked against two targets
/// yields two cached pipelines.
/// </summary>
/// <param name="Spirv">The compiled SPIR-V module containing both entry points, resolved once at
/// <c>DefineTechnique</c> — from an embedded resource (engine shaders) or disk (app shaders).</param>
/// <param name="VertexEntry">Vertex entry-point name.</param>
/// <param name="FragmentEntry">Fragment entry-point name.</param>
/// <param name="Bindings">Every descriptor the shader declares, grouped by category and carrying the slot the shader gave it. The per-category counts are properties over this, so the descriptor layout and the code that writes descriptors into it cannot come to disagree about where anything lives.</param>
/// <param name="HasVertexInput">True if the vertex stage reads a <see cref="Vertex"/> buffer (a mesh technique) rather than fabricating a fullscreen triangle.</param>
/// <param name="Blend">The blend equation — pairs with a pass that <c>.Blends</c> its target (alpha-over for transparency, additive for bloom).</param>
/// <param name="LinearFilter">Sample inputs with a linear (rather than nearest) sampler — smooth filtering for bloom's downsample/upsample; nearest elsewhere keeps the pixel grid crisp.</param>
/// <param name="HasFragmentPush">True if the shader's fragment (or, for a compute technique, compute) stage reads a small push-constant block (up to sixteen floats — <see cref="VulkanTechnique.FragmentPushSize"/>) — the per-pass scalar channel (JFA step, hovered id, exposure rate, a light-space matrix).</param>
/// <param name="IsCompute">True for a compute technique: one compute entry point (in <see cref="FragmentEntry"/>), dispatched rather than drawn.</param>
/// <param name="Address">Sampler wrap mode for the technique's combined image samplers — <see cref="SamplerAddress.Clamp"/> for most passes; repeat for tiling textures.</param>
/// <param name="Layout">Per-vertex stream layout, or null for fabricated-vertex techniques (fullscreen, particles) that have no vertex buffer.</param>
/// <param name="Instance">Per-instance stream layout, or null when the technique has no instance buffer.</param>
/// <param name="HasListPush">True if the technique reads a per-command tag rewrite after the shared push block (the draw-list path).</param>
/// <param name="Cull">Triangle face-culling mode — back-face culling for opaque mesh passes, none for fullscreen/billboard techniques.</param>
/// <param name="DepthTest">The depth comparison baked into the pipeline when the pass has a depth target.</param>
/// <param name="Bias">Constant + slope-scaled depth offset baked into the rasterizer state, or null for none.</param>
/// <param name="MaterialUniformSlot">Which uniform slot carries the per-material parameter block, or null when the shader declares none.</param>
/// <param name="MaterialParameterBytes">Bytes of per-material parameters to stage into the uniform ring per draw; 0 when the shader declares no block.</param>
/// <param name="SampleCompare">The compare function of the comparison samplers on the slots <see cref="ShaderBindingMap.ComparedTextures"/> marks (TechniqueDescriptor.SampleCompare).</param>
public readonly record struct VulkanTechniqueTemplate(
	byte[] Spirv,
	string VertexEntry,
	string FragmentEntry,
	ShaderBindingMap Bindings = default,
	bool HasVertexInput = false,
	BlendMode Blend = BlendMode.None,
	bool LinearFilter = false,
	bool HasFragmentPush = false,
	bool IsCompute = false,
	SamplerAddress Address = SamplerAddress.Clamp,
	VertexLayout? Layout = null,
	VertexLayout? Instance = null,
	bool HasListPush = false,
	CullMode Cull = CullMode.None,
	DepthCompare DepthTest = DepthCompare.LessOrEqual,
	DepthBias? Bias = null,
	int? MaterialUniformSlot = null,
	int MaterialParameterBytes = 0,
	DepthCompare? SampleCompare = null)
{
	/// <summary>Sampled inputs the shader reads, bound as combined image samplers.</summary>
	public int SampleCount => Bindings.SampledTextures.Length;

	/// <summary>Storage images the (compute) shader read-writes.</summary>
	public int StorageCount => Bindings.StorageImages.Length;

	/// <summary>Storage buffers (not images) the shader binds.</summary>
	public int StorageBufferCount => Bindings.StorageBuffers.Length;

	/// <summary>
	/// Descriptor-bound constant buffers the shader reads — the per-frame uniform seam, fed from the uniform ring;
	/// carries data too large for the push block (CSM matrices, lighting params, wind).
	/// </summary>
	public int UniformBufferCount => Bindings.UniformBuffers.Length;
}
