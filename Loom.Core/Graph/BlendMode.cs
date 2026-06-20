namespace Loom.Core.Graph;

/// <summary>
/// How a technique's colour output combines with what is already in the target — technique state, not pass
/// state (Planning.md §6/§7: "additive-ness is technique state, not pass state"). It pairs with a pass that
/// <c>.Blends</c> its target: the graph derives that the existing contents must be preserved and
/// read-modify-written; this enum picks the blend equation the backend bakes into the pipeline. Backend-neutral
/// — the backend maps each value to its own blend-state representation (Vulkan blend factors, D3D12 blend desc).
/// </summary>
public enum BlendMode
{
	/// <summary>Opaque — the source replaces the destination (blending off). The default.</summary>
	None,

	/// <summary>Alpha over-blend: <c>src.a·src + (1−src.a)·dst</c>. Forward transparency.</summary>
	AlphaOver,

	/// <summary>Additive: <c>src + dst</c>. Bloom upsample accumulation and composite.</summary>
	Additive,
}
