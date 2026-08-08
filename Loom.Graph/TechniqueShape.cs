using System.Collections.Immutable;

using Loom.Core.Materials;

namespace Loom.Graph;

/// <summary>
/// What a baked technique can accept, in backend-neutral terms — the return of a backend's bake step and the input to
/// the record-time checks. It exists so those checks live once instead of three times: every backend already computes
/// all of this while baking, so returning it costs nothing and, unlike a second accessor the backend must remember to
/// keep in step, it cannot drift from what was actually baked.
/// <para>
/// <strong>Every size here is in bytes</strong>, matching reflection and the backends' own block constants. A constant
/// buffer holds whatever struct the shader declared — matrices, integers, <c>float4</c> rows — so a float count is the
/// wrong unit to describe one. That the push API happens to take a <c>ReadOnlySpan&lt;float&gt;</c> is a property of
/// the call, not of the buffer; convert at the call site.
/// </para>
/// </summary>
/// <param name="Material">What the technique expects of a <see cref="Material"/>, from the same reflection.</param>
/// <param name="UniformBufferBytes">Each reflected constant buffer's declared size, in slot order.</param>
/// <param name="ScalarPushBytes">
/// Capacity of the fixed inline scalar block (Vulkan push constants / a DirectX 12 root-constant block). 0 when the
/// technique declares none, which means a push has nowhere to go unless a uniform slot is free.
/// </param>
/// <param name="ListPushBytes">Capacity of the draw-list push block, which also carries a trailing tag slot. 0 when the technique is not a draw-list technique.</param>
public readonly record struct TechniqueShape(
	MaterialLayout Material,
	ImmutableArray<int> UniformBufferBytes,
	int ScalarPushBytes,
	int ListPushBytes)
{
	/// <summary>
	/// The declared size of the constant buffer at <paramref name="slot"/>, or 0 when there is no such slot or the
	/// technique reflected no sidecar. 0 means "unknown", never "holds nothing".
	/// </summary>
	public int UniformSlotBytes(int slot) =>
		!UniformBufferBytes.IsDefaultOrEmpty && (uint)slot < (uint)UniformBufferBytes.Length ? UniformBufferBytes[slot] : 0;
}
