using Loom.Core.Geometry;
using Loom.Core.Graph;
using Loom.Core.Hosting;

namespace Loom.Core.Materials;

/// <summary>
/// A backend-neutral description of a drawing/dispatch technique, registered with the engine under a logical
/// name via <see cref="IEngine.DefineTechnique"/> and referenced from a pass closure by that name
/// (<c>ctx.FullScreen("lighting", …)</c>). This is the describe-layer technique vocabulary:
/// it names the <em>shader logically</em> — not by a backend artifact path — and states the technique's
/// <em>shape</em> (how many inputs it samples, whether it reads geometry, its blend equation). The backend
/// resolves <see cref="Shader"/> to its own bytecode (SPIR-V for Vulkan, DXIL for DX12) and
/// bakes the pipeline, so no graphics-API artifact name ever appears in app code.
/// <para>
/// The <em>descriptor-binding counts</em> (how many sampled inputs, storage images, and storage buffers the
/// shader binds) are <b>not stated here</b> — the backend derives them from the shader's reflection metadata,
/// so the app does not hand-count them. What remains are the
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
/// <param name="IsCompute">True for a compute technique: one compute entry point (in <see cref="FragmentEntry"/>, the slot it rides — <see cref="Compute"/> is the way to build one), dispatched rather than drawn.</param>
/// <param name="Address">How the technique's sampler treats out-of-range texture coordinates — <see cref="SamplerAddress.Clamp"/> (the default, used by every fullscreen/post pass) or <see cref="SamplerAddress.Repeat"/> for material techniques that tile a texture across geometry.</param>
/// <param name="Layout">The per-vertex layout the technique consumes. Defaults to <see cref="VertexLayout.MeshStandard"/> when <paramref name="HasVertexInput"/> is set and this is null, so existing pos/normal/uv techniques need no change. The mesh drawn with this technique must share the layout.</param>
/// <param name="Instance">The per-instance vertex stream a technique consumes, or null for a non-instanced technique. The backend places it at the layout's own <see cref="VertexLayout.Binding"/> — binding 1 alongside a per-vertex mesh (e.g. <see cref="VertexLayout.Transform"/>, the usual instanced draw), or binding 0 outright when there is no per-vertex mesh (a GPU-particle stream the compute fills). The instance data's stride must match its <see cref="VertexLayout.Stride"/>.</param>
/// <param name="HasListPush">True for a draw-list technique (<c>ctx.DrawList</c>): a small all-stage push block (vertex + fragment) whose final slot the backend rewrites with each command's <c>Tag</c>. Distinct from <paramref name="HasFragmentPush"/> (fragment-only scalar block) and the vertex-only MVP push.</param>
/// <param name="Cull">Which triangle faces the rasterizer discards — <see cref="CullMode.None"/> (the default, draw both faces), <see cref="CullMode.Back"/> for solid closed geometry, or <see cref="CullMode.Front"/>. Front faces are counter-clockwise; register a separate technique for a shader you want drawn both single- and double-sided.</param>
public readonly record struct TechniqueDescriptor(
	string Shader,
	string VertexEntry = "vertexMain",
	string FragmentEntry = "fragmentMain",
	bool HasVertexInput = false,
	BlendMode Blend = BlendMode.None,
	bool LinearFilter = false,
	bool HasFragmentPush = false,
	bool IsCompute = false,
	SamplerAddress Address = SamplerAddress.Clamp,
	VertexLayout? Layout = null,
	VertexLayout? Instance = null,
	bool HasListPush = false,
	CullMode Cull = CullMode.None)
{
	/// <summary>The conventional compute entry-point name, and the one the shader build compiles by.</summary>
	public const string DefaultComputeEntry = "computeMain";

	/// <summary>
	/// A compute technique: one entry point, dispatched via <c>ctx.Dispatch</c> rather than drawn. Name the entry as
	/// it appears in the shader source — the default matches the conventional <c>computeMain</c> the shader build
	/// compiles by. What a given target's bytecode ends up calling that entry internally is the backend's problem to
	/// resolve, not the author's.
	/// </summary>
	/// <param name="shader">Logical shader name; the backend resolves it to bytecode + reflection.</param>
	/// <param name="entry">The compute entry-point name as declared in the shader source.</param>
	/// <param name="hasFragmentPush">True if the compute stage reads the inline scalar block (up to sixteen floats).</param>
	public static TechniqueDescriptor Compute(string shader, string entry = DefaultComputeEntry, bool hasFragmentPush = false) =>
		new(shader, VertexEntry: entry, FragmentEntry: entry, IsCompute: true, HasFragmentPush: hasFragmentPush);

	/// <summary>
	/// The compute entry-point name for a technique built by <see cref="Compute"/> — an alias for
	/// <see cref="FragmentEntry"/>, the slot a compute entry rides, so backend code can read the intent rather than
	/// the storage.
	/// </summary>
	public string ComputeEntry => FragmentEntry;
}
