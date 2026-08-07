using Loom.Core.Geometry;
using Loom.Core.Graph;
using Loom.Core.Materials;

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
/// <param name="SampleCount">Number of sampled inputs the fragment reads (combined image samplers at set 0, bindings 0..N-1).</param>
/// <param name="HasVertexInput">True if the vertex stage reads a <see cref="Vertex"/> buffer (a mesh technique) rather than fabricating a fullscreen triangle.</param>
/// <param name="Blend">The blend equation — pairs with a pass that <c>.Blends</c> its target (alpha-over for transparency, additive for bloom).</param>
/// <param name="LinearFilter">Sample inputs with a linear (rather than nearest) sampler — smooth filtering for bloom's downsample/upsample; nearest elsewhere keeps the pixel grid crisp.</param>
/// <param name="HasFragmentPush">True if the shader's fragment (or, for a compute technique, compute) stage reads a small push-constant block (up to sixteen floats — <see cref="VulkanTechnique.FragmentPushSize"/>) — the per-pass scalar channel (JFA step, hovered id, exposure rate, a light-space matrix).</param>
/// <param name="IsCompute">True for a compute technique: one compute entry point (in <see cref="FragmentEntry"/>), dispatched rather than drawn.</param>
/// <param name="StorageCount">Number of storage images the (compute) shader read-writes, bound after the sampled inputs (bindings SampleCount..SampleCount+StorageCount-1).</param>
/// <param name="StorageBufferCount">Number of storage buffers (not images) the shader read-writes, bound after the storage images.</param>
/// <param name="Address">Sampler wrap mode for the technique's combined image samplers — <see cref="SamplerAddress.Clamp"/> for most passes; repeat for tiling textures.</param>
/// <param name="Layout">Per-vertex stream layout, or null for fabricated-vertex techniques (fullscreen, particles) that have no vertex buffer.</param>
/// <param name="Instance">Per-instance stream layout, or null when the technique has no instance buffer.</param>
/// <param name="UniformBufferCount">Number of descriptor-bound constant buffers the shader reads (the per-frame uniform seam), bound after the storage slots; 0 or 1 in practice. Fed from the per-frame uniform ring; carries data too large for the 16-float push (CSM matrices, lighting params, wind).</param>
/// <param name="HasListPush">True if the technique reads a per-command tag rewrite after the shared push block (the draw-list path).</param>
/// <param name="Cull">Triangle face-culling mode — back-face culling for opaque mesh passes, none for fullscreen/billboard techniques.</param>
/// <param name="DepthTest">The depth comparison baked into the pipeline when the pass has a depth target.</param>
/// <param name="Bias">Constant + slope-scaled depth offset baked into the rasterizer state, or null for none.</param>
/// <param name="MaterialUniformSlot">Which uniform slot carries the per-material parameter block, or null when the shader declares none.</param>
/// <param name="MaterialParameterBytes">Bytes of per-material parameters to stage into the uniform ring per draw; 0 when the shader declares no block.</param>
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
	SamplerAddress Address = SamplerAddress.Clamp,
	VertexLayout? Layout = null,
	VertexLayout? Instance = null,
	int UniformBufferCount = 0,
	bool HasListPush = false,
	CullMode Cull = CullMode.None,
	DepthCompare DepthTest = DepthCompare.LessOrEqual,
	DepthBias? Bias = null,
	int? MaterialUniformSlot = null,
	int MaterialParameterBytes = 0);
