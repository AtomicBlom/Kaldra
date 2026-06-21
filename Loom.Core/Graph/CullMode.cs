namespace Loom.Core.Graph;

/// <summary>
/// Which triangle faces a technique discards before rasterizing — fixed-function rasterizer state the backend
/// bakes into the pipeline, not pass state. Like <see cref="BlendMode"/> it is intent that reflection can't
/// infer, so the technique states it; the backend maps each value to its own representation (Vulkan
/// <c>VkCullModeFlags</c>, D3D12 <c>D3D12_CULL_MODE</c>).
/// <para>
/// Loom's front-face winding is <b>counter-clockwise</b> in screen space (the glTF convention and Vulkan's
/// default), and both backends are configured to agree on it — so <see cref="Back"/> discards the same
/// triangles whichever backend renders, preserving cross-backend parity.
/// </para>
/// </summary>
public enum CullMode
{
	/// <summary>Draw both faces — nothing is culled. The default, and how every technique behaved historically.</summary>
	None,

	/// <summary>Discard back-facing (clockwise) triangles — the usual setting for solid, closed opaque geometry.</summary>
	Back,

	/// <summary>Discard front-facing (counter-clockwise) triangles — e.g. viewing the inside of a skybox shell.</summary>
	Front,
}
