namespace Loom.Core;

/// <summary>
/// A backend-neutral description of a drawing/dispatch technique, registered with the engine under a logical
/// name via <see cref="IEngine.DefineTechnique"/> and referenced from a pass closure by that name
/// (<c>ctx.FullScreen("lighting", …)</c>). This is the describe-layer technique vocabulary (Planning.md §5):
/// it names the <em>shader logically</em> — not by a backend artifact path — and states the technique's
/// <em>shape</em> (how many inputs it samples, whether it reads geometry, its blend equation). The backend
/// resolves <see cref="Shader"/> to its own bytecode (SPIR-V for Vulkan, DXIL for a future DX12 backend) and
/// bakes the pipeline, so no graphics-API artifact name ever appears in app code.
/// <para>
/// The binding counts (<see cref="SampleCount"/>, <see cref="StorageImageCount"/>,
/// <see cref="StorageBufferCount"/>) are stated explicitly today; deriving them from shader reflection is the
/// M7-B4 follow-on, after which they become optional overrides rather than required hand-counts.
/// </para>
/// </summary>
/// <param name="Shader">Logical shader name (e.g. <c>"fullscreen"</c>); the backend resolves it to bytecode.</param>
/// <param name="VertexEntry">Vertex (or, for a graphics technique, vertex-stage) entry-point name.</param>
/// <param name="FragmentEntry">Fragment entry-point name — or, for a compute technique, the single compute entry point.</param>
/// <param name="SampleCount">Number of sampled inputs the shader reads, bound in <c>.Samples</c> order as the technique's 0..N-1 sampled inputs.</param>
/// <param name="HasVertexInput">True if the technique reads a <see cref="Vertex"/> stream (a mesh technique) rather than fabricating a fullscreen triangle.</param>
/// <param name="Blend">The blend equation — pairs with a pass that <c>.Blends</c> its target (alpha-over for transparency, additive for bloom).</param>
/// <param name="LinearFilter">Sample inputs with a linear (rather than nearest) filter — smooth filtering for bloom's down/upsample; nearest elsewhere keeps the pixel grid crisp.</param>
/// <param name="HasFragmentPush">True if the shader's fragment (or compute) stage reads a small inline scalar block (up to sixteen floats) — the per-pass scalar channel (JFA step, hovered id, exposure rate, a light-space matrix).</param>
/// <param name="IsCompute">True for a compute technique: one compute entry point (in <see cref="FragmentEntry"/>), dispatched rather than drawn (Planning.md §5 <c>.Dispatch</c>).</param>
/// <param name="StorageImageCount">Number of storage images the (compute) shader read-writes, bound after the sampled inputs.</param>
/// <param name="StorageBufferCount">Number of storage buffers the (compute) shader read-writes, bound after the storage images (GPU particles).</param>
/// <param name="ParticleInput">True if the technique reads a per-instance particle stream (a <c>float4</c> per particle) straight from a storage buffer.</param>
public readonly record struct TechniqueDescriptor(
	string Shader,
	string VertexEntry = "vertexMain",
	string FragmentEntry = "fragmentMain",
	int SampleCount = 0,
	bool HasVertexInput = false,
	BlendMode Blend = BlendMode.None,
	bool LinearFilter = false,
	bool HasFragmentPush = false,
	bool IsCompute = false,
	int StorageImageCount = 0,
	int StorageBufferCount = 0,
	bool ParticleInput = false);
