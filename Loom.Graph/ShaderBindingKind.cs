namespace Loom.Graph;

/// <summary>
/// What a descriptor-bound shader parameter is, classified from its reflected shape and access. The categories a
/// backend's layout builder switches on — read-only and read-write buffers are separate kinds because DirectX 12
/// binds them through different root-parameter types, a split Vulkan does not make but must not lose.
/// </summary>
public enum ShaderBindingKind
{
	/// <summary>A read-only texture: a combined image sampler on Vulkan, an SRV-table entry on DirectX 12.</summary>
	SampledTexture,

	/// <summary>A read-write texture (<c>RWTexture2D</c> and friends): a storage image, or a UAV.</summary>
	StorageImage,

	/// <summary>A read-only structured or byte-address buffer — a compute input.</summary>
	StorageBufferReadOnly,

	/// <summary>A read-write structured or byte-address buffer.</summary>
	StorageBufferReadWrite,

	/// <summary>A descriptor-bound <c>ConstantBuffer&lt;T&gt;</c>, distinct from a push/root constant block.</summary>
	UniformBuffer,
}
