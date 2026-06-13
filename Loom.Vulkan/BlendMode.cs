namespace Loom.Vulkan;

/// <summary>
/// How a technique's colour output combines with what is already in the target — a backend pipeline-state
/// property (Planning.md §6/§7: "additive-ness is technique state, not pass state"). It pairs with a pass
/// that <c>.Blends</c> its target (<c>LoadOp.Load</c>): the graph derives that the existing contents must be
/// preserved and read-modify-written; this enum picks the blend equation the backend bakes into the pipeline.
/// </summary>
public enum BlendMode
{
	/// <summary>Opaque — the source replaces the destination (<c>BlendEnable = false</c>). The default.</summary>
	None,

	/// <summary>Alpha over-blend: <c>src.a·src + (1−src.a)·dst</c>. Forward transparency.</summary>
	AlphaOver,

	/// <summary>Additive: <c>src + dst</c>. Bloom upsample accumulation and composite.</summary>
	Additive,
}
