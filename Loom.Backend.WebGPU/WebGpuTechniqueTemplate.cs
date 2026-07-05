using System.Runtime.Versioning;

using Loom.Core.Geometry;
using Loom.Core.Graph;
using Loom.Core.Materials;

namespace Loom.Backend.WebGPU;

/// <summary>
/// A named, format-agnostic WGSL technique, built once at <c>DefineTechnique</c>. It owns the format-independent
/// GPU objects — the shader module, the single (group 0) bind-group layout, and the pipeline layout — which are
/// created once here and shared by every per-target-format render pipeline baked from it (the render pipeline is
/// the only part that depends on the pass's attachment formats, mirroring the Vulkan template split). It also
/// carries the <see cref="WebGpuBindingModel"/> the render context uses to fill a bind group from a pass's inputs,
/// and the fixed-function intent (blend/cull/filter/vertex layout) the pipeline bakes in.
/// </summary>
[SupportedOSPlatform("browser")]
internal sealed class WebGpuTechniqueTemplate : IDisposable
{
	private readonly WebGpuHandleAllocator _handles;

	public required WebGpuBindingModel Bindings { get; init; }
	public required string VertexEntry { get; init; }
	public required string FragmentEntry { get; init; }
	public required bool HasVertexInput { get; init; }
	public required BlendMode Blend { get; init; }
	public required bool LinearFilter { get; init; }
	public required SamplerAddress Address { get; init; }
	public required bool IsCompute { get; init; }
	public required VertexLayout? Layout { get; init; }
	public required VertexLayout? Instance { get; init; }
	public required CullMode Cull { get; init; }

	/// <summary>The shader-module handle (both entry points).</summary>
	public required int ShaderModule { get; init; }

	/// <summary>The group-0 bind-group-layout handle, or -1 when the technique binds nothing.</summary>
	public required int BindGroupLayout { get; init; }

	/// <summary>The pipeline-layout handle (wraps the bind-group layout, or empty when there are no bindings).</summary>
	public required int PipelineLayout { get; init; }

	/// <summary>Number of sampled inputs the technique reads — the count a pass must supply to <c>FullScreen</c>/<c>Draw</c>.</summary>
	public int SampleCount => Bindings.SampledTextureBindings.Length;

	public WebGpuTechniqueTemplate(WebGpuHandleAllocator handles) => _handles = handles;

	/// <summary>
	/// Resolve a logical shader to its WGSL + reflection, parse the binding model, and create the module +
	/// (group 0) layouts on the JS side. Wrapped in a validation error scope by the caller so a malformed layout
	/// surfaces as a named error rather than a black frame.
	/// </summary>
	public static WebGpuTechniqueTemplate Create(WebGpuHandleAllocator handles, string shaderName, string wgsl, byte[]? reflectionJson, in TechniqueDescriptor descriptor)
	{
		var bindings = WebGpuBindingModel.Parse(wgsl, reflectionJson, shaderName);

		var module = handles.Allocate(WebGpuHandleAllocator.ShaderModules);
		WebGpuInterop.CreateShaderModule(module, wgsl, shaderName);

		var bindGroupLayout = -1;
		var pipelineLayout = handles.Allocate(WebGpuHandleAllocator.PipelineLayouts);
		if (bindings.HasBindings)
		{
			bindGroupLayout = handles.Allocate(WebGpuHandleAllocator.BindGroupLayouts);
			WebGpuInterop.CreateBindGroupLayout(bindGroupLayout, bindings.LayoutEntryData(descriptor.IsCompute));
			Span<int> layouts = [bindGroupLayout];
			WebGpuInterop.CreatePipelineLayout(pipelineLayout, layouts);
		}
		else
		{
			WebGpuInterop.CreatePipelineLayout(pipelineLayout, []);
		}

		var layout = descriptor.Layout ?? (descriptor.HasVertexInput ? VertexLayout.MeshStandard : null);
		return new WebGpuTechniqueTemplate(handles)
		{
			Bindings = bindings,
			VertexEntry = descriptor.VertexEntry,
			FragmentEntry = descriptor.FragmentEntry,
			HasVertexInput = descriptor.HasVertexInput,
			Blend = descriptor.Blend,
			LinearFilter = descriptor.LinearFilter,
			Address = descriptor.Address,
			IsCompute = descriptor.IsCompute,
			Layout = layout,
			Instance = descriptor.Instance,
			Cull = descriptor.Cull,
			ShaderModule = module,
			BindGroupLayout = bindGroupLayout,
			PipelineLayout = pipelineLayout,
		};
	}

	public void Dispose()
	{
		_handles.Release(WebGpuHandleAllocator.PipelineLayouts, PipelineLayout);
		if (BindGroupLayout >= 0)
			_handles.Release(WebGpuHandleAllocator.BindGroupLayouts, BindGroupLayout);
		_handles.Release(WebGpuHandleAllocator.ShaderModules, ShaderModule);
	}
}
