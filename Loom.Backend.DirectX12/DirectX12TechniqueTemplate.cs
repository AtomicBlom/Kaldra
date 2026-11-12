using Loom.Core.Geometry;
using Loom.Core.Graph;
using Loom.Core.Materials;
using Loom.Graph;

namespace Loom.Backend.DirectX12;

/// <summary>
/// A registered DirectX12 technique: its <em>prebuilt per-stage bytecode</em> + the shape needed to bake a pipeline
/// (the DirectX12 counterpart of <c>TechniqueTemplate</c>). The bytecode is signed DXIL the Loom build emitted
/// offline from the Slang shaders — a blob the PSO consumes directly, so baking never compiles and the backend
/// ships no shader compiler. The vertex and fragment stages are separate blobs; for
/// a compute technique only <see cref="FragmentBytecode"/> (the compute blob) is used. <see cref="Bindings"/> is the
/// reflected binding map, which the record path holds a pass's storage buffers to.
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
	DepthCompare DepthTest = DepthCompare.LessOrEqual, DepthBias? Bias = null,
	int? MaterialUniformSlot = null, int MaterialParameterBytes = 0,
	bool[]? ComparedInputs = null, DepthCompare? SampleCompare = null, bool AlphaToCoverage = false,
	ShaderBindingMap Bindings = default);
