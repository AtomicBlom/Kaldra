using Loom.Core;

namespace Loom.DX12;

/// <summary>
/// A registered DX12 technique: its <em>prebuilt per-stage bytecode</em> + the shape needed to bake a pipeline
/// (the DX12 counterpart of <c>TechniqueTemplate</c>). The bytecode is either signed DXIL the Loom build emitted
/// offline (the real Slang shaders) or DXBC the backend FXC-compiled from a built-in stand-in — both are blobs
/// the PSO consumes directly, so baking never compiles. The vertex and fragment stages are separate blobs; for
/// a compute technique only <see cref="FragmentBytecode"/> (the compute blob) is used.
/// </summary>
internal readonly record struct DX12TechniqueTemplate(
	byte[] VertexBytecode, byte[] FragmentBytecode, string VertexEntry, string FragmentEntry, int InputCount,
	bool HasVertexInput, bool HasFragmentPush, bool LinearFilter,
	bool IsCompute = false, bool ParticleInput = false, BlendMode Blend = BlendMode.None,
	int StorageImages = 0, int StorageBuffers = 0, int StorageBufferStride = 0,
	SamplerAddress Address = SamplerAddress.Clamp,
	Loom.Core.VertexLayout? Layout = null, Loom.Core.VertexLayout? Instance = null);
