namespace BinaryVibrance.Kaldra.Core.Graph;

/// <summary>
/// How a technique's colour output combines with what is already in the target — technique state, not pass
/// state ("additive-ness is technique state, not pass state"). It pairs with a pass that
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

	/// <summary>
	/// Premultiplied alpha-over: <c>src + (1−src.a)·dst</c> — the source colour is already multiplied by its
	/// own alpha, so it is added rather than scaled again. The UI compositor folds element opacity into the
	/// vertex colour, so its draw list blends this way.
	/// </summary>
	PremultipliedAlpha,

	/// <summary>Multiply: <c>src·dst</c>. The source colour is multiplied by the destination colour, darkening
	/// the result. Used for shadow overlays and colour-grading effects.</summary>
	Multiply,
}
