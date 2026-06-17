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
/// The <em>descriptor-binding counts</em> (how many sampled inputs, storage images, and storage buffers the
/// shader binds) are <b>not stated here</b> — the backend derives them from the shader's reflection metadata
/// (the M7-B4 "Slang's own reflection" decision), so the app no longer hand-counts them. What remains are the
/// pieces reflection can't infer: the logical shader/entry names and the fixed-function pipeline <em>intent</em>
/// (does it read geometry, how does it blend, which sampler filter).
/// </para>
/// </summary>
/// <param name="Shader">Logical shader name (e.g. <c>"fullscreen"</c>); the backend resolves it to bytecode + reflection.</param>
/// <param name="VertexEntry">Vertex (or, for a graphics technique, vertex-stage) entry-point name.</param>
/// <param name="FragmentEntry">Fragment entry-point name — or, for a compute technique, the single compute entry point.</param>
/// <param name="HasVertexInput">True if the technique reads a <see cref="Vertex"/> stream (a mesh technique) rather than fabricating a fullscreen triangle.</param>
/// <param name="Blend">The blend equation — pairs with a pass that <c>.Blends</c> its target (alpha-over for transparency, additive for bloom).</param>
/// <param name="LinearFilter">Sample inputs with a linear (rather than nearest) filter — smooth filtering for bloom's down/upsample; nearest elsewhere keeps the pixel grid crisp.</param>
/// <param name="HasFragmentPush">True if the shader's fragment (or compute) stage reads a small inline scalar block (up to sixteen floats) — the per-pass scalar channel (JFA step, hovered id, exposure rate, a light-space matrix).</param>
/// <param name="IsCompute">True for a compute technique: one compute entry point (in <see cref="FragmentEntry"/>), dispatched rather than drawn (Planning.md §5 <c>.Dispatch</c>).</param>
/// <param name="ParticleInput">True if the technique reads a per-instance particle stream (a <c>float4</c> per particle) straight from a storage buffer.</param>
/// <param name="Layout">The per-vertex layout the technique consumes (M12). Defaults to <see cref="VertexLayout.MeshStandard"/> when <paramref name="HasVertexInput"/> is set and this is null, so existing pos/normal/uv techniques need no change. The mesh drawn with this technique must share the layout.</param>
/// <param name="Instance">The per-instance layout for an instanced technique (M12) — non-null marks the technique as instanced and adds a second vertex stream at binding 1 (e.g. <see cref="VertexLayout.Transform"/>). The <c>ctx.DrawInstanced</c> data's stride must match its <see cref="VertexLayout.Stride"/>.</param>
public readonly record struct TechniqueDescriptor(
	string Shader,
	string VertexEntry = "vertexMain",
	string FragmentEntry = "fragmentMain",
	bool HasVertexInput = false,
	BlendMode Blend = BlendMode.None,
	bool LinearFilter = false,
	bool HasFragmentPush = false,
	bool IsCompute = false,
	bool ParticleInput = false,
	VertexLayout? Layout = null,
	VertexLayout? Instance = null);
