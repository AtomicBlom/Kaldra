namespace BinaryVibrance.Kaldra.Graph;

/// <summary>One descriptor-bound shader parameter, as the reflection sidecar declares it.</summary>
/// <param name="Name">The shader-side name — how the reserved per-material block is recognised.</param>
/// <param name="Index">
/// The slot this parameter occupies, in the numbering of the reflection's
/// <see cref="ShaderReflection.Target"/>. Read from the sidecar rather than inferred from position, which is what
/// frees a shader from having to declare its resources grouped by category.
/// </param>
/// <param name="Kind">The category, from the reflected shape and access.</param>
/// <param name="SlotCount">
/// How many <em>consecutive</em> slots this parameter occupies — Vulkan's <c>descriptorCount</c>, DirectX 12's
/// <c>NumDescriptors</c>, and the span a WGSL combined sampler covers. Named for the slots rather than for an array
/// length because the two coincide under SPIR-V (an array of N textures is N slots) and diverge under WGSL (one
/// <c>Sampler2D</c> is a single array element but two slots), and the slot span is what a layout builder needs.
/// 1 unless the sidecar says otherwise.
/// </param>
/// <param name="ByteSize">A constant buffer's laid-out size in bytes, padded to its alignment; 0 for every other kind.</param>
/// <param name="ElementStride">A structured buffer's element size in bytes, its fields packed as laid out (the end of
/// its last field); 0 for a raw buffer, a texture, a constant buffer, or an element the sidecar can't size.</param>
/// <param name="ElementAlignment">A structured buffer's element alignment in bytes under std430 (its widest field's), so
/// an array of it may pad each element to a multiple of this; 0 where <paramref name="ElementStride"/> is.</param>
public readonly record struct ShaderBinding(
	string Name,
	int Index,
	ShaderBindingKind Kind,
	int SlotCount = 1,
	int ByteSize = 0,
	int ElementStride = 0,
	int ElementAlignment = 0)
{
	/// <summary>
	/// Whether a storage buffer of <paramref name="elementSize"/>-byte elements can be what this structured buffer reads:
	/// its element size packed, or padded to its alignment, as the app's struct may be either. True when either side is
	/// unknown (0), since there is then nothing to hold it to.
	/// </summary>
	public bool AcceptsElementSize(int elementSize)
	{
		if (ElementStride <= 0 || elementSize <= 0)
			return true;
		if (elementSize == ElementStride)
			return true;
		var alignment = Math.Max(ElementAlignment, 1);
		return elementSize == (ElementStride + alignment - 1) / alignment * alignment;
	}
}
