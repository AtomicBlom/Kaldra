using Loom.Core.Geometry;
using Loom.Core.Graph;
using Loom.Core.Materials;

namespace Loom.Backend.DirectX12;

/// <summary>
/// A registered DirectX12 technique: its <em>prebuilt per-stage bytecode</em> + the shape needed to bake a pipeline
/// (the DirectX12 counterpart of <c>TechniqueTemplate</c>). The bytecode is either signed DXIL the Loom build emitted
/// offline (the real Slang shaders) or DXBC the backend FXC-compiled from a built-in stand-in — both are blobs
/// the PSO consumes directly, so baking never compiles. The vertex and fragment stages are separate blobs; for
/// a compute technique only <see cref="FragmentBytecode"/> (the compute blob) is used.
/// </summary>
internal readonly record struct DirectX12TechniqueTemplate(
	byte[] VertexBytecode, byte[] FragmentBytecode, string VertexEntry, string FragmentEntry, int InputCount,
	bool HasVertexInput, bool HasFragmentPush, bool LinearFilter,
	bool IsCompute = false, BlendMode Blend = BlendMode.None,
	int StorageImages = 0, int StorageBuffers = 0, int StorageBufferStride = 0,
	SamplerAddress Address = SamplerAddress.Clamp,
	VertexLayout? Layout = null, VertexLayout? Instance = null,
	int UniformBufferCount = 0, int InputBufferCount = 0, bool HasListPush = false,
	CullMode Cull = CullMode.None,
	DepthCompare DepthTest = DepthCompare.LessOrEqual, DepthBias? Bias = null);
