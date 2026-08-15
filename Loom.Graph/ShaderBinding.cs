namespace Loom.Graph;

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
/// <param name="ElementStride">A structured buffer's element stride in bytes; 0 for a raw buffer, a texture, or a constant buffer.</param>
public readonly record struct ShaderBinding(
	string Name,
	int Index,
	ShaderBindingKind Kind,
	int SlotCount = 1,
	int ByteSize = 0,
	int ElementStride = 0);
